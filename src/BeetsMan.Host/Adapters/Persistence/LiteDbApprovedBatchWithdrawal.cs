using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists withdrawal on the same transaction lane as native start and approval admission.</summary>
/// <remarks>2026-11-25: Fence outstanding and delayed approvals for FR-LIBRARY-002#ac-batch-revert.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string BatchWithdrawalReceipt = "WithdrawnLibraryBatch";

    /// <inheritdoc />
    Task<HostOutcome<WithdrawApprovedLibraryBatchReceipt>> IApprovedLibraryBatchStore.WithdrawBatchAsync(Guid library, AuthenticatedHostInstance caller,
        Guid operation, WithdrawApprovedLibraryBatchRequest request, CancellationToken token) => ExecuteAsync<WithdrawApprovedLibraryBatchReceipt>(_ =>
    {
        if (!IsImportCaller(library, caller) || caller.InstanceId != request.OriginInstanceId)
            return new HostOutcome<WithdrawApprovedLibraryBatchReceipt>.Forbidden();
        var origin = caller.InstanceId;
        var key = OperationId(new(origin, operation));
        var journals = BatchJournals(database);
        if (journals.FindById(key) is { } row)
        {
            if (!MatchesBatchReview(row, library, origin, request.BatchId, request.BatchRevision, request.ReviewId))
                return Conflict("operation-intent-mismatch");
            var status = ReadStored<ApprovedLibraryBatchStatus>(row["Status"]);
            if (status.Phase is BatchPublicationPhase.Accepted or BatchPublicationPhase.Rejected)
                SaveBatchPhase(row, status with { Phase = BatchPublicationPhase.Withdrawn, FailureCode = null });
            else if (status.Phase != BatchPublicationPhase.Withdrawn)
                return Conflict("library-batch-commit-in-progress");
        }
        else
        {
            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            if (receipts.Exists(Query.EQ("_id", key)))
                return Conflict("operation-intent-mismatch");
            var batchKey = BatchKey(library, origin, request.BatchId);
            if (journals.Exists(Query.EQ("BatchKey", batchKey)))
                return Conflict("library-batch-already-admitted");
            var status = new ApprovedLibraryBatchStatus(library, origin, operation, request.BatchId, request.BatchRevision,
                request.ReviewId, BatchPublicationPhase.Withdrawn, null, [], 0, []);
            journals.Insert(new BsonDocument { ["_id"] = key, ["BatchKey"] = batchKey, ["LibraryId"] = library, ["OriginInstanceId"] = origin,
                ["OperationId"] = operation, ["BatchId"] = request.BatchId, ["BatchRevision"] = request.BatchRevision, ["ReviewId"] = request.ReviewId,
                ["Phase"] = nameof(BatchPublicationPhase.Withdrawn), ["EntryCount"] = 0, ["ImportPartCount"] = 0, ["Status"] = JsonSerializer.Serialize(status) });
            SaveBatchReservation(receipts, origin, operation, operation, HashKey($"withdraw-library-batch/v1/{library:N}/" + JsonSerializer.Serialize(request)),
                BatchWithdrawalReceipt);
        }
        return new HostOutcome<WithdrawApprovedLibraryBatchReceipt>.Success(new(library, origin, operation, request.BatchId, request.BatchRevision, request.ReviewId));

        static HostOutcome<WithdrawApprovedLibraryBatchReceipt> Conflict(string code) => new HostOutcome<WithdrawApprovedLibraryBatchReceipt>.Rejected(code, Conflict: true);
    }, token);

    /// <summary>Scopes the batch's unique journal key to its registered owner and library.</summary>
    private static string BatchKey(Guid library, Guid origin, Guid batch) => $"{library:N}/{origin:N}/{batch:N}";

    /// <summary>Compares the exact immutable review identity before a pre-write state transition.</summary>
    private static bool MatchesBatchReview(BsonDocument row, Guid library, Guid origin, Guid batch, long revision, Guid review) =>
        row["LibraryId"].AsGuid == library && row["OriginInstanceId"].AsGuid == origin && row["BatchId"].AsGuid == batch &&
        row["BatchRevision"].AsInt64 == revision && row["ReviewId"].AsGuid == review;
}
