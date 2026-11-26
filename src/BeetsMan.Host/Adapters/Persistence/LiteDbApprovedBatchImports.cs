using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Scopes verified import receipts to the exact admitted operation, entry, and ordinal.</summary>
/// <remarks>2026-11-24: Reopen only the confirmed copies during retry for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IApprovedBatchImportStore
{
    private const string ApprovedBatchImportParts = "library_batch_import_parts";

    /// <inheritdoc />
    Task<HostOutcome<ApprovedBatchImportTarget>> IApprovedBatchImportStore.ReadBatchImportPartAsync(Guid library, AuthenticatedHostInstance caller,
        Guid approval, int position, int index, Guid file, CancellationToken token) => OnLaneAsync(() =>
        ReadBatchImportPart(library, caller, approval, position, index, file), token);

    /// <inheritdoc />
    Task<HostOutcome<ApprovedBatchImportPartReceipt>> IApprovedBatchImportStore.RecordBatchImportPartAsync(AuthenticatedHostInstance caller,
        ApprovedBatchImportPartReceipt receipt, CancellationToken token) => ExecuteAsync<ApprovedBatchImportPartReceipt>(_ =>
    {
        var target = ReadBatchImportPart(receipt.LibraryId, caller, receipt.ApprovalOperationId, receipt.Position, receipt.Index, receipt.FileId);
        if (target is not HostOutcome<ApprovedBatchImportTarget>.Success { Value: var selected })
            return target.PropagateFailure<ApprovedBatchImportPartReceipt>();
        if (selected.Receipt != receipt)
            return new HostOutcome<ApprovedBatchImportPartReceipt>.Rejected("batch-import-part-mismatch", Conflict: true);
        var key = OperationId(new(receipt.OriginInstanceId, receipt.ApprovalOperationId));
        var part = string.Create(CultureInfo.InvariantCulture, $"{key}/{receipt.Position}/{receipt.Index}");
        var table = database.GetCollection<BsonDocument>(ApprovedBatchImportParts);
        if (table.FindById(part) is { } prior)
            return ReadStored<ApprovedBatchImportPartReceipt>(prior["Receipt"]) == receipt
                ? new HostOutcome<ApprovedBatchImportPartReceipt>.Success(receipt)
                : new HostOutcome<ApprovedBatchImportPartReceipt>.Rejected("batch-import-part-mismatch", Conflict: true);
        table.Insert(new BsonDocument { ["_id"] = part, ["PublicationKey"] = key, ["Position"] = receipt.Position,
            ["Index"] = receipt.Index, ["Receipt"] = JsonSerializer.Serialize(receipt) });
        return new HostOutcome<ApprovedBatchImportPartReceipt>.Success(receipt);
    }, token);

    private ApprovedLibraryBatchStatus ProjectBatchImportStatus(BsonDocument approval)
    {
        var key = approval["_id"].AsString;
        var status = ReadStored<ApprovedLibraryBatchStatus>(approval["Status"]);
        var count = approval["ImportPartCount"].IsInt32 ? approval["ImportPartCount"].AsInt32 : database.GetCollection<BsonDocument>(ApprovedBatchEntries)
            .Find(Query.And(Query.EQ("PublicationKey", key), Query.EQ("Kind", "Import")))
            .Sum(row => ((ApprovedBatchChange.Import)ReadStored<ApprovedBatchChange>(row["Payload"])).Files.Length);
        return status with { ImportPartCount = count, ImportReviews = ReadBatchImportReviews(key), TransferredImports =
            [.. database.GetCollection<BsonDocument>(ApprovedBatchImportParts).Find(Query.EQ("PublicationKey", key))
                .OrderBy(row => row["Position"].AsInt32).ThenBy(row => row["Index"].AsInt32)
                .Select(row => ReadStored<ApprovedBatchImportPartReceipt>(row["Receipt"]))] };
    }

    private HostOutcome<ApprovedBatchImportTarget> ReadBatchImportPart(Guid library, AuthenticatedHostInstance caller, Guid approval,
        int position, int index, Guid file)
    {
        if (!IsImportCaller(library, caller))
            return new HostOutcome<ApprovedBatchImportTarget>.Forbidden();
        var key = OperationId(new(caller.InstanceId, approval));
        var journal = database.GetCollection<BsonDocument>(ApprovedBatchPublications).FindById(key);
        if (journal is null || journal["LibraryId"].AsGuid != library || Text(journal, "Phase") != nameof(BatchPublicationPhase.Accepted))
            return Missing();
        var entry = database.GetCollection<BsonDocument>(ApprovedBatchEntries).FindById(string.Create(CultureInfo.InvariantCulture, $"{key}/{position}"));
        if (entry is null || Text(entry, "Kind") != "Import" || ReadStored<ApprovedBatchChange>(entry["Payload"]) is not ApprovedBatchChange.Import import ||
            index < 0 || index >= import.Files.Length || import.Files[index].FileId != file)
            return Missing();
        var child = entry["EntryOperationId"].AsGuid;
        var receipt = new ApprovedBatchImportPartReceipt(library, caller.InstanceId, approval, child, position, index, file);
        return new HostOutcome<ApprovedBatchImportTarget>.Success(new(receipt, BuildBatchUpload(library, caller.InstanceId, child, import)));

        static HostOutcome<ApprovedBatchImportTarget> Missing() => new HostOutcome<ApprovedBatchImportTarget>.Rejected("batch-import-part-unavailable");
    }

    /// <summary>Projects the same ordered manifest for private transfers and the guided review worker.</summary>
    private static ImportUpload BuildBatchUpload(Guid library, Guid origin, Guid entry, ApprovedBatchChange.Import import) => new(library, origin, entry,
        [.. import.Files.Select(part => part.Manifest with { Sha256 = part.Manifest.Sha256.ToUpperInvariant() })]);
}
