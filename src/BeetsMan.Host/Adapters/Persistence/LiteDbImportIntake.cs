using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the authorized upload manifest, accepted job, and idempotency receipt.</summary>
/// <remarks>2026-11-24: Implement the STB08 upload transaction for FR-BEETS-001#ac-import and ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IImportIntakeStore
{
    private const string ImportUploads = "import_uploads";
    private const string LibraryJobs = "library_jobs";
    private const string ImportReceiptType = "StartedGuidedImport";

    /// <inheritdoc />
    Task<HostOutcome<ImportUploadReceipt>> IImportIntakeStore.BeginUploadAsync(ImportUpload upload, AuthenticatedHostInstance caller,
        CancellationToken token) => ExecuteAsync<ImportUploadReceipt>(_ =>
        {
            if (!IsImportCaller(upload.LibraryId, caller) || caller.InstanceId != upload.OwnerId)
                return new HostOutcome<ImportUploadReceipt>.Forbidden();
            var table = database.GetCollection<BsonDocument>(ImportUploads);
            var key = UploadKey(upload.LibraryId, caller.InstanceId, upload.UploadId);
            if (table.FindById(key) is { } current)
            {
                return ReadUpload(current).Files.SequenceEqual(upload.Files)
                    ? new HostOutcome<ImportUploadReceipt>.Success(new(upload.UploadId))
                    : new HostOutcome<ImportUploadReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
            }
            table.Insert(new BsonDocument { ["_id"] = key, ["LibraryId"] = upload.LibraryId, ["OriginInstanceId"] = caller.InstanceId,
                ["UploadId"] = upload.UploadId, ["Files"] = JsonSerializer.Serialize(upload.Files) });
            return new HostOutcome<ImportUploadReceipt>.Success(new(upload.UploadId));
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<ImportUpload>> IImportIntakeStore.ReadUploadAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid uploadId,
        CancellationToken token) => OnLaneAsync<ImportUpload>(() =>
        {
            if (!IsImportCaller(libraryId, caller))
                return new HostOutcome<ImportUpload>.Forbidden();
            var row = database.GetCollection<BsonDocument>(ImportUploads).FindById(UploadKey(libraryId, caller.InstanceId, uploadId));
            return row is null ? new HostOutcome<ImportUpload>.Rejected("import-upload-unavailable") :
                new HostOutcome<ImportUpload>.Success(ReadUpload(row));
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<StartImportReceipt?>> IImportIntakeStore.ReadStartReceiptAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StartImportRequest request, CancellationToken token) => OnLaneAsync(() => ReadImportReceipt(libraryId, caller, request), token);

    /// <inheritdoc />
    Task<HostOutcome<StartImportReceipt>> IImportIntakeStore.AcceptImportAsync(ImportUpload verified, AuthenticatedHostInstance caller,
        StartImportRequest request, CancellationToken token) => ExecuteAsync<StartImportReceipt>(_ =>
        {
            var prior = ReadImportReceipt(verified.LibraryId, caller, request);
            if (prior is not HostOutcome<StartImportReceipt?>.Success { Value: var existing })
                return prior.PropagateFailure<StartImportReceipt>();
            if (existing is not null)
                return new HostOutcome<StartImportReceipt>.Success(existing);
            var uploads = database.GetCollection<BsonDocument>(ImportUploads);
            var row = uploads.FindById(UploadKey(verified.LibraryId, caller.InstanceId, request.UploadId));
            if (row is null || verified.OwnerId != caller.InstanceId || verified.UploadId != request.UploadId ||
                !ReadUpload(row).Files.SequenceEqual(verified.Files))
                return new HostOutcome<StartImportReceipt>.Rejected("import-upload-unavailable");
            var jobId = row["JobId"].IsGuid ? row["JobId"].AsGuid : Guid.NewGuid();
            if (!row["JobId"].IsGuid)
            {
                database.GetCollection<BsonDocument>(LibraryJobs).Insert(new BsonDocument
                {
                    ["_id"] = jobId, ["LibraryId"] = verified.LibraryId, ["OriginInstanceId"] = caller.InstanceId,
                    ["OperationId"] = request.OperationId, ["Kind"] = "GuidedImport", ["Status"] = nameof(ImportJobPhase.Accepted),
                    ["UploadId"] = request.UploadId, ["Revision"] = 1L
                });
                row["JobId"] = jobId;
                uploads.Update(row);
            }
            var result = new StartImportReceipt(request.OperationId, jobId, verified.LibraryId, 1);
            database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
            {
                ["_id"] = OperationId(new(caller.InstanceId, request.OperationId)), ["OriginInstanceId"] = caller.InstanceId,
                ["OperationId"] = request.OperationId, ["IntentFingerprint"] = ImportIntent(verified.LibraryId, request.UploadId),
                ["Receipt"] = new BsonDocument { ["Type"] = ImportReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<StartImportReceipt>.Success(result);
        }, token);

    /// <inheritdoc />
    /// <remarks>2026-11-24: Expose the edit job to its submitting desktop for FR-BEETS-001#ac-edit and ac-recover.</remarks>
    Task<HostOutcome<ImportJobSnapshot>> IImportIntakeStore.ReadImportJobAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid jobId,
        CancellationToken token) => OnLaneAsync<ImportJobSnapshot>(() =>
        {
            if (!IsImportCaller(libraryId, caller))
                return new HostOutcome<ImportJobSnapshot>.Forbidden();
            var row = FindScopedLibraryJob(database.GetCollection<BsonDocument>(LibraryJobs), libraryId, jobId);
            if (row is null || row["OriginInstanceId"].AsGuid != caller.InstanceId || Text(row, "Kind") is not ("GuidedImport" or TrackTagEditKind or MaintenanceJobKind))
                return new HostOutcome<ImportJobSnapshot>.Rejected("library-job-unavailable");
            return new HostOutcome<ImportJobSnapshot>.Success(ProjectImportJob(row));
        }, token);

    private HostOutcome<StartImportReceipt?> ReadImportReceipt(Guid libraryId, AuthenticatedHostInstance caller, StartImportRequest request)
    {
        if (request.OriginInstanceId != caller.InstanceId || !IsImportCaller(libraryId, caller))
            return new HostOutcome<StartImportReceipt?>.Forbidden();
        var row = database.GetCollection<BsonDocument>("operation_receipts").FindById(OperationId(new(caller.InstanceId, request.OperationId)));
        if (row is null)
            return new HostOutcome<StartImportReceipt?>.Success(null);
        var receipt = row["Receipt"].AsDocument;
        return Text(row, "IntentFingerprint") == ImportIntent(libraryId, request.UploadId) && Text(receipt, "Type") == ImportReceiptType &&
            receipt["Version"].IsInt32 && receipt["Version"].AsInt32 == 1
            ? new HostOutcome<StartImportReceipt?>.Success(ReadStored<StartImportReceipt>(receipt["Result"]))
            : new HostOutcome<StartImportReceipt?>.Rejected("operation-intent-mismatch", Conflict: true);
    }

    private bool IsImportCaller(Guid libraryId, AuthenticatedHostInstance caller) => IsPublicationCaller(libraryId, caller) &&
        Text(database.GetCollection<BsonDocument>("registered_instances").FindById(caller.InstanceId), "Role") == "Desktop";

    private static ImportUpload ReadUpload(BsonDocument row) => new(row["LibraryId"].AsGuid, row["OriginInstanceId"].AsGuid, row["UploadId"].AsGuid,
        JsonSerializer.Deserialize<ImmutableArray<ImportUploadFile>>(row["Files"].AsString), row["JobId"].IsGuid ? row["JobId"].AsGuid : null);

    private static string UploadKey(Guid libraryId, Guid owner, Guid upload) => $"{libraryId:N}/{owner:N}/{upload:N}";
    private static string ImportIntent(Guid libraryId, Guid upload) => $"guided-import/v1/{libraryId:N}/{upload:N}";
}
