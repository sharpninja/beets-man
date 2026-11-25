using System.Collections.Immutable;
using BeetsMan.Host.Application;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists path-level sender proof and receiver observations for one published transfer.</summary>
/// <remarks>2026-11-24: Resume receiver verification after restart for FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportReceiverLease?>> ClaimReceiverAsync(DateTimeOffset now, CancellationToken token) =>
        ExecuteAsync<DeviceExportReceiverLease?>(_ =>
        {
            var claim = ClaimWork(DeviceExportScanState.AwaitingReceiver, now, token);
            if (claim is not HostOutcome<DeviceExportScanLease?>.Success { Value: var lease })
                return claim.PropagateFailure<DeviceExportReceiverLease?>();
            if (lease is null)
                return new HostOutcome<DeviceExportReceiverLease?>.Success(null);

            var table = database.GetCollection<BsonDocument>(ExportScanCollection);
            var row = table.FindById(lease.Checkpoint.TransferId);
            var items = ReadTransferItems(lease.Checkpoint.TransferId).OrderBy(item => item["Path"].AsString, StringComparer.Ordinal).ToImmutableArray();
            if (!row["ItemCount"].IsInt32 || items.IsEmpty || items.Length != row["ItemCount"].AsInt32)
            {
                UpdateScan(table, row, DeviceExportScanState.Blocked, "export-transfer-paths-unavailable", now.UtcDateTime);
                return new HostOutcome<DeviceExportReceiverLease?>.Success(null);
            }

            return new HostOutcome<DeviceExportReceiverLease?>.Success(new(lease, [.. items.Select(item => new DeviceExportTransferItem(
                item["Path"].AsString, Text(item, "ContentIdentity"), item["Bytes"].IsInt64 ? item["Bytes"].AsInt64 : null, Text(item, "Version")))]));
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<bool>> RecordReceiverAsync(DeviceExportReceiverLease work, ImmutableArray<DeviceExportTransferItem> items,
        DeviceExportReceiverProgress? progress, string? failureCode, bool blocked, DateTimeOffset now, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var table = database.GetCollection<BsonDocument>(ExportScanCollection);
        var transferId = work.Lease.Checkpoint.TransferId;
        var row = table.FindById(transferId);
        var utc = now.UtcDateTime;
        if (row is null || !MatchesLease(row, work.Lease, utc))
            return new HostOutcome<bool>.Success(false);

        if (ScanScopeFailure(row, work.Lease.Checkpoint) is { } changed)
        {
            UpdateScan(table, row, changed.State, changed.Code, utc);
            return new HostOutcome<bool>.Success(true);
        }

        var state = blocked ? DeviceExportScanState.Blocked : progress?.Completed == true ? DeviceExportScanState.Completed : DeviceExportScanState.AwaitingReceiver;
        if (!items.SequenceEqual(work.Items))
            SaveTransferProofs(transferId, work.Items, items);
        if (progress is { } evidence)
        {
            row["BytesVerified"] = evidence.BytesVerified;
            row["TotalBytes"] = evidence.TotalBytes;
        }
        var due = state == DeviceExportScanState.AwaitingReceiver ? utc.AddSeconds(10) : utc;
        UpdateScan(table, row, state, failureCode, due);
        return new HostOutcome<bool>.Success(true);
    }, token);

    private void WriteTransferItems(Guid id, Dictionary<string, string?> paths)
    {
        var table = database.GetCollection<BsonDocument>(ExportScanItems);
        foreach (var (path, identity) in paths)
            table.Insert(new BsonDocument
            {
                ["_id"] = TransferItemId(id, path), ["TransferId"] = id, ["Path"] = path,
                ["ContentIdentity"] = identity is null ? BsonValue.Null : new BsonValue(identity)
            });
    }

    private void SaveTransferProofs(Guid id, ImmutableArray<DeviceExportTransferItem> expected, ImmutableArray<DeviceExportTransferItem> proofs)
    {
        if (proofs.Length != expected.Length)
            throw new IOException("The sender proof omits expected export paths.");
        var rows = database.GetCollection<BsonDocument>(ExportScanItems);
        for (var index = 0; index < expected.Length; index++)
        {
            var proof = proofs[index];
            var prior = expected[index];
            if (proof.Path != prior.Path || proof.ContentIdentity != prior.ContentIdentity || proof.Bytes is not >= 0 || proof.Version is null ||
                prior.Bytes is not null && proof != prior)
                throw new IOException("The sender proof changed an expected export path.");
            var row = rows.FindById(TransferItemId(id, proof.Path));
            row["Bytes"] = proof.Bytes.Value;
            row["Version"] = proof.Version;
            rows.Update(row);
        }
    }

    private IEnumerable<BsonDocument> ReadTransferItems(Guid id) => database.GetCollection<BsonDocument>(ExportScanItems)
        .Find(Query.EQ("TransferId", id));

    private static string TransferItemId(Guid id, string path) => $"{id:N}/{HashKey(path)}";
}
