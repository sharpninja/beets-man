using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Stores job execution, failure and retry checkpoints on the library's LiteDB lane.</summary>
/// <remarks>2026-11-24: Fence native retries behind stored intent and observed recovery for FR-BEETS-001#ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IGuidedImportWorkStore
{
    private const string RetryImportReceiptType = "RetriedGuidedImport";

    /// <inheritdoc />
    Task<HostOutcome<GuidedImportWork?>> IGuidedImportWorkStore.ReadNextImportAsync(Guid libraryId, CancellationToken token) =>
        OnLaneAsync<GuidedImportWork?>(() =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var active = jobs.Find(Query.And(Query.EQ("LibraryId", libraryId), Query.EQ("Kind", "GuidedImport"),
                Query.In("Status", new BsonArray(nameof(ImportJobPhase.Running), nameof(ImportJobPhase.Accepted)))));
            var job = active.OrderByDescending(row => ReadImportPhase(row) == ImportJobPhase.Running).ThenBy(row => row["_id"].AsGuid).FirstOrDefault();
            if (job is null)
                return new HostOutcome<GuidedImportWork?>.Success(null);
            var uploadId = job["UploadId"].AsGuid;
            var upload = database.GetCollection<BsonDocument>(ImportUploads).FindById(UploadKey(libraryId, job["OriginInstanceId"].AsGuid, uploadId));
            if (upload is null || !upload["JobId"].IsGuid || upload["JobId"].AsGuid != job["_id"].AsGuid)
                return new HostOutcome<GuidedImportWork?>.Unavailable("import-upload-unavailable");
            var choice = job["ApprovedChoice"].IsDocument ? job["ApprovedChoice"].AsDocument : null;
            return new HostOutcome<GuidedImportWork?>.Success(new(ProjectImportJob(job), ReadUpload(upload),
                choice?["OperationId"].AsGuid, choice is null ? null : Enum.Parse<ImportChoiceKind>(choice["Decision"].AsString),
                Text(choice, "ProviderKey"), WasNativeAttempted(job),
                choice is not null && choice["NativeMatch"].IsString ? ReadStored<ApprovedBeetsMatch>(choice["NativeMatch"]) : null,
                job["NativePreparation"].IsString ? ReadStored<PreparedImportAlbum>(job["NativePreparation"]) : null));
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> IGuidedImportWorkStore.RecordNativeAttemptAsync(GuidedImportWork work, PreparedImportAlbum? prepared, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentImportWork(jobs, work);
        if (row is null || work.Job.Phase != ImportJobPhase.Running || WasNativeAttempted(row) ||
            (work.Choice == ImportChoiceKind.UseMatch && (prepared is null || work.ApprovedMatch is null)))
            return new HostOutcome<bool>.Success(false);
        row["NativeAttempted"] = true;
        if (prepared is not null)
            row["NativePreparation"] = JsonSerializer.Serialize(prepared);
        jobs.Update(row);
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> IGuidedImportWorkStore.FailImportAsync(GuidedImportWork work, string code, bool knownNotStarted,
        CancellationToken token) => ExecuteAsync<bool>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = FindCurrentImportWork(jobs, work);
            if (row is null)
                return new HostOutcome<bool>.Success(false);
            row["FailedFromPhase"] = work.Job.Phase.ToString();
            row["FailureCode"] = code;
            row["Retryable"] = true;
            row["Status"] = nameof(ImportJobPhase.Failed);
            row["Revision"] = checked(work.Job.Revision + 1);
            if (knownNotStarted)
                row["NativeAttempted"] = false;
            jobs.Update(row);
            return new HostOutcome<bool>.Success(true);
        }, token);

    /// <inheritdoc />
    /// <remarks>2026-11-24: Retry a tag job on the existing job route for FR-BEETS-001#ac-recover.</remarks>
    /// <remarks>2026-11-24: Reopen the same catalog-only removal for FR-BEETS-001#ac-recover.</remarks>
    /// <remarks>2026-11-24: Reopen the same organization and pinned manifest for FR-BEETS-001#ac-recover.</remarks>
    Task<HostOutcome<RetryImportReceipt>> IGuidedImportWorkStore.RetryImportAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        RetryImportRequest request, CancellationToken token) => ExecuteAsync<RetryImportReceipt>(_ =>
        {
            if (!IsImportCaller(libraryId, caller) || request.OriginInstanceId != caller.InstanceId)
                return new HostOutcome<RetryImportReceipt>.Forbidden();
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = FindScopedLibraryJob(jobs, libraryId, jobId);
            if (row is null || row["OriginInstanceId"].AsGuid != caller.InstanceId ||
                (Text(row, "Kind") is not ("GuidedImport" or TrackTagEditKind) && !IsRemovalJob(row) && !IsOrganizationJob(row)))
                return new HostOutcome<RetryImportReceipt>.Rejected("library-job-unavailable");

            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            var operation = OperationId(new(caller.InstanceId, request.OperationId));
            var fingerprint = string.Create(CultureInfo.InvariantCulture, $"guided-import-retry/v1/{libraryId:N}/{jobId:N}/{request.ExpectedRevision}");
            if (receipts.FindById(operation) is { } saved)
            {
                var payload = saved["Receipt"].AsDocument;
                return Text(saved, "IntentFingerprint") == fingerprint && Text(payload, "Type") == RetryImportReceiptType && payload["Version"].AsInt32 == 1
                    ? new HostOutcome<RetryImportReceipt>.Success(ReadStored<RetryImportReceipt>(payload["Result"]))
                    : new HostOutcome<RetryImportReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
            }

            var revision = row["Revision"].AsInt64;
            var resume = Text(row, "FailedFromPhase");
            var tagEdit = Text(row, "Kind") == TrackTagEditKind;
            if (ReadImportPhase(row) != ImportJobPhase.Failed || revision != request.ExpectedRevision || !row["Retryable"].AsBoolean ||
                resume is not (nameof(ImportJobPhase.Accepted) or nameof(ImportJobPhase.Running)))
                return new HostOutcome<RetryImportReceipt>.Rejected(tagEdit ? "stale-track-tag-retry" :
                    IsRemovalJob(row) ? row["AudioDeletionApproved"].AsBoolean ? "stale-audio-removal-retry" : "stale-catalog-removal-retry" :
                    IsOrganizationJob(row) ? "stale-organization-retry" : "stale-import-retry",
                    revision, Conflict: true);
            if (tagEdit && PendingTrackTagJobs(jobs, libraryId, row["TrackId"].AsGuid).Any(job => job["_id"].AsGuid != jobId))
                return new HostOutcome<RetryImportReceipt>.Rejected("track-tag-edit-pending", revision, Conflict: true);

            var result = new RetryImportReceipt(request.OperationId, jobId, libraryId, checked(revision + 1));
            row["Status"] = resume;
            row["Revision"] = result.Revision;
            row.Remove("FailedFromPhase");
            row.Remove("FailureCode");
            row.Remove("Retryable");
            jobs.Update(row);
            receipts.Insert(new BsonDocument
            {
                ["_id"] = operation, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId,
                ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument
                { ["Type"] = RetryImportReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<RetryImportReceipt>.Success(result);
        }, token);

    private static bool WasNativeAttempted(BsonDocument job) => job["NativeAttempted"].IsBoolean && job["NativeAttempted"].AsBoolean;

    private static BsonDocument? FindCurrentImportWork(ILiteCollection<BsonDocument> jobs, GuidedImportWork work)
    {
        var row = FindScopedImportJob(jobs, work.Job.LibraryId, work.Job.JobId);
        return row is not null && ReadImportPhase(row) == work.Job.Phase && row["Revision"].AsInt64 == work.Job.Revision &&
            row["UploadId"].AsGuid == work.Upload.UploadId ? row : null;
    }
}
