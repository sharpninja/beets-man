using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Journals the private batch import review and decision without creating direct import jobs.</summary>
/// <remarks>2026-11-24: Recover explicit import choices for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IApprovedBatchImportReviewStore
{
    private const string BatchImportChoiceReceipt = "ApprovedBatchImportChoice";

    /// <inheritdoc />
    Task<HostOutcome<ApprovedBatchImportReviewWork?>> IApprovedBatchImportReviewStore.ReadNextBatchImportReviewAsync(Guid library, CancellationToken token) =>
        OnLaneAsync<ApprovedBatchImportReviewWork?>(() =>
        {
            var approvals = BatchJournals(database).Find(Query.And(Query.EQ("LibraryId", library), Query.EQ("Phase", nameof(BatchPublicationPhase.Accepted))))
                .OrderBy(row => row["_id"].AsString, StringComparer.Ordinal);
            foreach (var approval in approvals)
            {
                var origin = approval["OriginInstanceId"].AsGuid;
                if (!IsImportCaller(library, new(library, origin)))
                    continue;
                var key = approval["_id"].AsString;
                foreach (var entry in BatchImportEntries(key))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry["ImportReview"].IsString)
                        continue;
                    var import = (ApprovedBatchChange.Import)ReadStored<ApprovedBatchChange>(entry["Payload"]);
                    if (!HasAllReviewCopies(approval, entry, import))
                        continue;
                    return new HostOutcome<ApprovedBatchImportReviewWork?>.Success(new(approval["OperationId"].AsGuid, entry["Position"].AsInt32,
                        BuildBatchUpload(library, origin, entry["EntryOperationId"].AsGuid, import)));
                }
            }
            return new HostOutcome<ApprovedBatchImportReviewWork?>.Success(null);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> IApprovedBatchImportReviewStore.OfferBatchImportReviewAsync(ApprovedBatchImportReviewWork work,
        ImmutableArray<BeetsImportCandidate> candidates, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var upload = work.Upload;
        var key = OperationId(new(upload.OwnerId, work.ApprovalOperationId));
        var caller = new AuthenticatedHostInstance(upload.LibraryId, upload.OwnerId);
        var approval = BatchJournals(database).FindById(key);
        if (!IsImportCaller(upload.LibraryId, caller) || approval is null || approval["LibraryId"].AsGuid != upload.LibraryId ||
            Text(approval, "Phase") != nameof(BatchPublicationPhase.Accepted))
            return new HostOutcome<bool>.Success(false);
        var entries = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
        if (entries.FindById(BatchImportEntryKey(key, work.Position)) is not { } entry || Text(entry, "Kind") != "Import" ||
            entry["EntryOperationId"].AsGuid != upload.UploadId || entry["ImportReview"].IsString)
            return new HostOutcome<bool>.Success(false);
        var import = (ApprovedBatchChange.Import)ReadStored<ApprovedBatchChange>(entry["Payload"]);
        var immutable = BuildBatchUpload(upload.LibraryId, upload.OwnerId, upload.UploadId, import);
        if (!immutable.Files.SequenceEqual(upload.Files) || !HasAllReviewCopies(approval, entry, import))
            return new HostOutcome<bool>.Unavailable("batch-import-review-copies-changed");
        entry["ImportReview"] = JsonSerializer.Serialize(CreateStoredImportReview([.. immutable.Files.Select(file => file.Name)], candidates));
        entry["ImportReviewRevision"] = 1L;
        return new HostOutcome<bool>.Success(entries.Update(entry));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<ApprovedBatchImportChoiceReceipt>> IApprovedBatchImportReviewStore.ChooseBatchImportAsync(Guid library,
        AuthenticatedHostInstance caller, Guid approval, int position, ChooseApprovedBatchImportRequest request, CancellationToken token) =>
        ExecuteAsync<ApprovedBatchImportChoiceReceipt>(_ =>
        {
            if (!IsImportCaller(library, caller) || caller.InstanceId != request.OriginInstanceId)
                return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Forbidden();
            var key = OperationId(new(caller.InstanceId, approval));
            var publication = BatchJournals(database).FindById(key);
            var entries = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
            if (publication is null || publication["LibraryId"].AsGuid != library ||
                entries.FindById(BatchImportEntryKey(key, position)) is not { } entry || Text(entry, "Kind") != "Import")
                return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("batch-import-review-unavailable");
            var operation = OperationId(new(caller.InstanceId, request.OperationId));
            var intent = HashKey(FormattableString.Invariant($"batch-import-choice/v1/{library:N}/{approval:N}/{position}/") + JsonSerializer.Serialize(request));
            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            if (receipts.FindById(operation) is { } existing)
            {
                var payload = existing["Receipt"].AsDocument;
                return Text(existing, "IntentFingerprint") == intent && Text(payload, "Type") == BatchImportChoiceReceipt && payload["Version"].AsInt32 == 1
                    ? new HostOutcome<ApprovedBatchImportChoiceReceipt>.Success(ReadStored<ApprovedBatchImportChoiceReceipt>(payload["Result"]))
                    : new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
            }
            if (entry["EntryOperationId"].AsGuid != request.EntryOperationId || !entry["ImportReview"].IsString)
                return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("batch-import-review-unavailable");
            var revision = entry["ImportReviewRevision"].AsInt64;
            var review = ReadStored<StoredImportReview>(entry["ImportReview"]);
            if (revision != request.ExpectedRevision || review.Snapshot.ItemId != request.ReviewItemId || entry["ImportChoice"].IsString ||
                Text(publication, "Phase") != nameof(BatchPublicationPhase.Accepted))
                return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("stale-batch-import-review", revision, Conflict: true);
            if (request.MatchId is { } id)
            {
                if (!review.ProviderKeys.ContainsKey(id))
                    return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("unknown-import-match");
                if (review.NativeMatches?.ContainsKey(id) != true || review.Snapshot.Matches.Single(match => match.MatchId == id).Comparison?.CanApply != true)
                    return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("incomplete-import-match");
            }
            var result = new ApprovedBatchImportChoiceReceipt(library, caller.InstanceId, approval, request.EntryOperationId, request.OperationId,
                position, checked(revision + 1), request.ReviewItemId, request.Choice, request.MatchId);
            entry["ImportChoice"] = JsonSerializer.Serialize(result);
            entry["ImportReviewRevision"] = result.Revision;
            entries.Update(entry);
            receipts.Insert(new BsonDocument { ["_id"] = operation, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId,
                ["IntentFingerprint"] = intent, ["Receipt"] = new BsonDocument { ["Type"] = BatchImportChoiceReceipt, ["Version"] = 1,
                    ["Result"] = JsonSerializer.Serialize(result) } });
            return new HostOutcome<ApprovedBatchImportChoiceReceipt>.Success(result);
        }, token);

    /// <summary>Returns only the public review and decision assigned to each ordered entry.</summary>
    private ImmutableArray<ApprovedBatchImportReview> ReadBatchImportReviews(string key) => [.. BatchImportEntries(key)
        .Where(entry => entry["ImportReview"].IsString).Select(entry =>
        {
            var review = ReadStored<StoredImportReview>(entry["ImportReview"]);
            var choice = entry["ImportChoice"].IsString ? ReadStored<ApprovedBatchImportChoiceReceipt>(entry["ImportChoice"]) : null;
            return new ApprovedBatchImportReview(entry["Position"].AsInt32, entry["EntryOperationId"].AsGuid, entry["ImportReviewRevision"].AsInt64,
                review.Snapshot, choice?.Choice, choice?.MatchId);
        })];

    /// <summary>Checks every copy receipt against the approved entry, owner, and file identities.</summary>
    private bool HasAllReviewCopies(BsonDocument approval, BsonDocument entry, ApprovedBatchChange.Import import)
    {
        if (import.Files.IsDefaultOrEmpty)
            return false;
        var key = approval["_id"].AsString;
        var position = entry["Position"].AsInt32;
        var receipts = database.GetCollection<BsonDocument>(ApprovedBatchImportParts);
        for (var index = 0; index < import.Files.Length; index++)
        {
            var expected = new ApprovedBatchImportPartReceipt(approval["LibraryId"].AsGuid, approval["OriginInstanceId"].AsGuid,
                approval["OperationId"].AsGuid, entry["EntryOperationId"].AsGuid, position, index, import.Files[index].FileId);
            if (receipts.FindById(FormattableString.Invariant($"{key}/{position}/{index}")) is not { } row ||
                ReadStored<ApprovedBatchImportPartReceipt>(row["Receipt"]) != expected)
                return false;
        }
        return true;
    }

    /// <summary>Reads entries by position under the approved operation key.</summary>
    private IEnumerable<BsonDocument> BatchImportEntries(string key) => database.GetCollection<BsonDocument>(ApprovedBatchEntries)
        .Find(Query.And(Query.EQ("PublicationKey", key), Query.EQ("Kind", "Import"))).OrderBy(row => row["Position"].AsInt32);

    private static string BatchImportEntryKey(string key, int position) => FormattableString.Invariant($"{key}/{position}");
}
