using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Journals the album review and approved choice on the host's existing LiteDB transaction lane.</summary>
/// <remarks>2026-11-24: Persist STB08 choices before external effects for FR-BEETS-001#ac-import and ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IImportReviewStore
{
    private const string ImportChoiceReceiptType = "ChoseGuidedImport";

    /// <inheritdoc />
    Task<HostOutcome<ImportJobSnapshot>> IImportReviewStore.OfferAsync(Guid libraryId, Guid jobId, Guid uploadId,
        ImmutableArray<BeetsImportCandidate> candidates, CancellationToken token) => ExecuteAsync<ImportJobSnapshot>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var job = FindScopedImportJob(jobs, libraryId, jobId);
            if (job is null || job["UploadId"].AsGuid != uploadId)
                return new HostOutcome<ImportJobSnapshot>.Rejected("library-job-unavailable");

            var status = ReadImportPhase(job);
            if (status == ImportJobPhase.ReviewRequired)
                return new HostOutcome<ImportJobSnapshot>.Success(ProjectImportJob(job));
            if (status != ImportJobPhase.Accepted)
                return new HostOutcome<ImportJobSnapshot>.Rejected("import-review-unavailable", job["Revision"].AsInt64, Conflict: true);

            var upload = database.GetCollection<BsonDocument>(ImportUploads)
                .FindById(UploadKey(libraryId, job["OriginInstanceId"].AsGuid, uploadId));
            if (upload is null || !upload["JobId"].IsGuid || upload["JobId"].AsGuid != jobId)
                return new HostOutcome<ImportJobSnapshot>.Unavailable("import-upload-unavailable");
            var names = ReadUpload(upload).Files.Select(file => file.Name).ToImmutableArray();
            if (candidates.Any(candidate => candidate.Comparison is { } comparison && comparison.Tracks.Length != names.Length))
                return new HostOutcome<ImportJobSnapshot>.Rejected("invalid-import-proposals");

            job["Review"] = JsonSerializer.Serialize(CreateStoredImportReview(names, candidates));
            job["Status"] = nameof(ImportJobPhase.ReviewRequired);
            job["Revision"] = checked(job["Revision"].AsInt64 + 1);
            jobs.Update(job);
            return new HostOutcome<ImportJobSnapshot>.Success(ProjectImportJob(job));
        }, token);

    /// <summary>Assigns public identities and keeps the native match data private in either review workflow.</summary>
    private static StoredImportReview CreateStoredImportReview(ImmutableArray<string> names, ImmutableArray<BeetsImportCandidate> candidates)
    {
        var matches = ImmutableArray.CreateBuilder<ImportReviewMatch>(candidates.Length);
        var providerKeys = ImmutableDictionary.CreateBuilder<Guid, string>();
        var nativeMatches = ImmutableDictionary.CreateBuilder<Guid, ApprovedBeetsMatch>();
        foreach (var candidate in candidates)
        {
            var matchId = Guid.NewGuid();
            matches.Add(new(matchId, candidate.Title, candidate.Artist, candidate.Comparison));
            providerKeys.Add(matchId, candidate.ProviderKey);
            if (candidate.NativeMatch is { } native)
                nativeMatches.Add(matchId, native);
        }
        return new(new(Guid.NewGuid(), names, matches.MoveToImmutable()), providerKeys.ToImmutable(), nativeMatches.ToImmutable());
    }

    /// <inheritdoc />
    Task<HostOutcome<ChooseImportReceipt>> IImportReviewStore.ChooseAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        ChooseImportRequest request, CancellationToken token) => ExecuteAsync<ChooseImportReceipt>(_ =>
        {
            if (!IsImportCaller(libraryId, caller) || request.OriginInstanceId != caller.InstanceId)
                return new HostOutcome<ChooseImportReceipt>.Forbidden();
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var job = FindScopedImportJob(jobs, libraryId, jobId);
            if (job is null || job["OriginInstanceId"].AsGuid != caller.InstanceId)
                return new HostOutcome<ChooseImportReceipt>.Rejected("library-job-unavailable");

            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            var operationKey = OperationId(new(caller.InstanceId, request.OperationId));
            var intent = ImportChoiceIntent(libraryId, jobId, request);
            if (receipts.FindById(operationKey) is { } saved)
                return ReplayImportChoice(saved, intent);

            var revision = job["Revision"].AsInt64;
            if (revision != request.ExpectedRevision || ReadImportPhase(job) != ImportJobPhase.ReviewRequired)
                return new HostOutcome<ChooseImportReceipt>.Rejected("stale-import-review", revision, Conflict: true);
            var review = ReadStored<StoredImportReview>(job["Review"]);
            if (review.Snapshot.ItemId != request.ReviewItemId)
                return new HostOutcome<ChooseImportReceipt>.Rejected("stale-import-review", revision, Conflict: true);
            string? providerKey = null;
            if (request.MatchId is Guid matchId && !review.ProviderKeys.TryGetValue(matchId, out providerKey))
                return new HostOutcome<ChooseImportReceipt>.Rejected("unknown-import-match");
            if (request.MatchId is Guid selectedMatch && review.Snapshot.Matches.Any(match => match.MatchId == selectedMatch && match.Comparison?.CanApply == false))
                return new HostOutcome<ChooseImportReceipt>.Rejected("incomplete-import-match");

            revision = checked(revision + 1);
            var result = new ChooseImportReceipt(request.OperationId, jobId, libraryId, revision);
            job["ApprovedChoice"] = new BsonDocument
            {
                ["OperationId"] = request.OperationId, ["ReviewItemId"] = request.ReviewItemId, ["ReviewRevision"] = request.ExpectedRevision,
                ["Decision"] = request.Choice.ToString(), ["ProviderKey"] = providerKey
            };
            if (request.MatchId is Guid nativeId && review.NativeMatches?.TryGetValue(nativeId, out var nativeMatch) == true)
                job["ApprovedChoice"].AsDocument["NativeMatch"] = JsonSerializer.Serialize(nativeMatch);
            job["Status"] = (request.Choice == ImportChoiceKind.Skip ? ImportJobPhase.Skipped : ImportJobPhase.Running).ToString();
            job["Revision"] = revision;
            jobs.Update(job);
            receipts.Insert(new BsonDocument
            {
                ["_id"] = operationKey, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId,
                ["IntentFingerprint"] = intent,
                ["Receipt"] = new BsonDocument { ["Type"] = ImportChoiceReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<ChooseImportReceipt>.Success(result);
        }, token);

    private static HostOutcome<ChooseImportReceipt> ReplayImportChoice(BsonDocument saved, string intent)
    {
        var receipt = saved["Receipt"].AsDocument;
        return Text(saved, "IntentFingerprint") == intent && Text(receipt, "Type") == ImportChoiceReceiptType &&
            receipt["Version"].IsInt32 && receipt["Version"].AsInt32 == 1
            ? new HostOutcome<ChooseImportReceipt>.Success(ReadStored<ChooseImportReceipt>(receipt["Result"]))
            : new HostOutcome<ChooseImportReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
    }

    private static string ImportChoiceIntent(Guid libraryId, Guid jobId, ChooseImportRequest request) =>
        string.Create(CultureInfo.InvariantCulture,
            $"guided-import-choice/v1/{libraryId:N}/{jobId:N}/{request.ReviewItemId:N}/{request.ExpectedRevision}/{request.Choice}/{request.MatchId:N}");

    private static BsonDocument? FindScopedImportJob(ILiteCollection<BsonDocument> jobs, Guid libraryId, Guid jobId)
        => FindScopedLibraryJob(jobs, libraryId, jobId) is { } row && Text(row, "Kind") == "GuidedImport" ? row : null;

    /// <summary>Finds a job only within the requested library for FR-BEETS-001#ac-recover.</summary>
    private static BsonDocument? FindScopedLibraryJob(ILiteCollection<BsonDocument> jobs, Guid libraryId, Guid jobId)
    {
        var row = jobs.FindById(jobId);
        return row is not null && row["LibraryId"].IsGuid && row["LibraryId"].AsGuid == libraryId &&
            row["OriginInstanceId"].IsGuid ? row : null;
    }

    private static ImportJobPhase ReadImportPhase(BsonDocument job) => Enum.Parse<ImportJobPhase>(job["Status"].AsString);

    private static ImportJobSnapshot ProjectImportJob(BsonDocument job)
    {
        var phase = ReadImportPhase(job);
        return new ImportJobSnapshot(job["_id"].AsGuid, job["LibraryId"].AsGuid, job["Revision"].AsInt64, phase,
            job["PublishedRevision"].IsNumber ? job["PublishedRevision"].AsInt64 : null,
            Text(job, "FailureCode"), job["Retryable"].IsBoolean && job["Retryable"].AsBoolean,
            phase == ImportJobPhase.ReviewRequired ? ReadStored<StoredImportReview>(job["Review"]).Snapshot : null);
    }

    /// <summary>Stores public match projections with their private provider identifiers.</summary>
    private sealed record StoredImportReview(ImportAlbumReview Snapshot, ImmutableDictionary<Guid, string> ProviderKeys,
        ImmutableDictionary<Guid, ApprovedBeetsMatch>? NativeMatches = null);
}
