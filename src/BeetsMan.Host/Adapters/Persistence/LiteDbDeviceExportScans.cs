using BeetsMan.Host.Application;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Queues and leases scans on the same database lane as the published export.</summary>
/// <remarks>2026-11-24: Persist a retryable post-publication scan for FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string ExportScanCollection = "device_export_scans";
    private const string ExportScanItems = "device_export_scan_items";
    private const string QueuedScan = nameof(DeviceExportScanState.Queued);

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportScanCheckpoint>> ReadScanAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid transferId,
        CancellationToken token) => OnLaneAsync<DeviceExportScanCheckpoint>(() =>
        {
            var read = ReadAuthorizedScanRow(libraryId, caller, transferId);
            return read is HostOutcome<BsonDocument>.Success { Value: var row }
                ? new HostOutcome<DeviceExportScanCheckpoint>.Success(ReadScan(row))
                : read.PropagateFailure<DeviceExportScanCheckpoint>();
        }, token);

    private HostOutcome<BsonDocument> ReadAuthorizedScanRow(Guid libraryId, AuthenticatedHostInstance caller, Guid transferId)
    {
        if (!IsPublicationCaller(libraryId, caller))
            return new HostOutcome<BsonDocument>.Forbidden();
        var row = database.GetCollection<BsonDocument>(ExportScanCollection).FindById(transferId);
        return row is not null && row["LibraryId"].AsGuid == libraryId ? new HostOutcome<BsonDocument>.Success(row) :
            new HostOutcome<BsonDocument>.Rejected("export-transfer-unavailable");
    }

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportScanLease?>> ClaimScanAsync(DateTimeOffset now, CancellationToken token) =>
        ExecuteAsync<DeviceExportScanLease?>(_ => ClaimWork(DeviceExportScanState.Queued, now, token), token);

    private HostOutcome<DeviceExportScanLease?> ClaimWork(DeviceExportScanState state, DateTimeOffset now, CancellationToken token)
    {
        var table = database.GetCollection<BsonDocument>(ExportScanCollection);
        var utc = now.UtcDateTime;
        var due = table.Find(Query.EQ("State", state.ToString())).Where(row => row["DueUtcTicks"].AsInt64 <= utc.Ticks && !LeaseActive(row, utc))
            .OrderBy(row => row["DueUtcTicks"].AsInt64).ThenBy(row => row["ManifestRevision"].AsInt64).ThenBy(row => row["_id"].AsGuid);
        foreach (var row in due)
        {
            token.ThrowIfCancellationRequested();
            var work = ReadScan(row);
            var destination = work.Destination;
            var scope = row["ManifestKey"].AsString;
            if (HasPendingPublication(destination) || HasLeasedScan(scope, utc))
                continue;
            if (ReadRegisteredAndroidExport(destination.LibraryId, destination.DeviceId) != destination)
            {
                UpdateScan(table, row, DeviceExportScanState.Blocked, "export-registration-changed", utc);
                continue;
            }
            if (ManifestRevision(scope) != work.ManifestRevision)
            {
                UpdateScan(table, row, DeviceExportScanState.Superseded, "export-manifest-changed", utc);
                continue;
            }

            var lease = Guid.NewGuid();
            row["LeaseId"] = lease;
            row["LeaseUntilUtcTicks"] = (utc + DeviceExportScanDispatcher.LeaseDuration).Ticks;
            row["Revision"] = checked(work.Revision + 1);
            table.Update(row);
            return new HostOutcome<DeviceExportScanLease?>.Success(new(ReadScan(row), lease));
        }
        return new HostOutcome<DeviceExportScanLease?>.Success(null);
    }

    /// <inheritdoc />
    public Task<HostOutcome<bool>> RecordScanAsync(DeviceExportScanLease lease, DeviceExportScanState state, string? failureCode, DateTimeOffset now,
        CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var table = database.GetCollection<BsonDocument>(ExportScanCollection);
        var row = table.FindById(lease.Checkpoint.TransferId);
        if (row is null || !MatchesLease(row, lease, now.UtcDateTime))
            return new HostOutcome<bool>.Success(false);
        if (ScanScopeFailure(row, lease.Checkpoint) is { } changed)
            (state, failureCode) = changed;
        var attempts = checked(row["Attempts"].AsInt32 + 1);
        row["Attempts"] = attempts;
        var due = state == DeviceExportScanState.Queued ? now.UtcDateTime.AddSeconds(1 << Math.Min(attempts, 6)) : now.UtcDateTime;
        UpdateScan(table, row, state, failureCode, due);
        return new HostOutcome<bool>.Success(true);
    }, token);

    private Guid QueueDeviceExportScan(PendingDeviceExportPublication publication, string scope, long manifestRevision)
    {
        var paths = new Dictionary<string, string?>(publication.Destination.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var operation in publication.Operations)
        {
            if (operation.Kind is DeviceExportOperationKind.Remove or DeviceExportOperationKind.Move && operation.Before is { } prior)
                paths[prior.Path] = null;
            if (operation.After is { } after)
                paths[after.Path] = after.ContentIdentity;
        }
        database.GetCollection<BsonDocument>(ExportScanCollection).Insert(new BsonDocument
        {
            ["_id"] = publication.PublicationId, ["LibraryId"] = publication.Destination.LibraryId, ["ManifestKey"] = scope,
            ["PlanId"] = publication.PlanId, ["PlanRevision"] = publication.PlanRevision, ["ManifestRevision"] = manifestRevision,
            ["Destination"] = JsonSerializer.Serialize(publication.Destination), ["State"] = QueuedScan,
            ["Attempts"] = 0, ["Revision"] = 1L, ["DueUtcTicks"] = DateTime.UnixEpoch.Ticks, ["ItemCount"] = paths.Count
        });
        WriteTransferItems(publication.PublicationId, paths);
        return publication.PublicationId;
    }

    private (DeviceExportScanState State, string Code)? ScanScopeFailure(BsonDocument row, DeviceExportScanCheckpoint checkpoint)
    {
        var destination = checkpoint.Destination;
        if (ReadRegisteredAndroidExport(destination.LibraryId, destination.DeviceId) != destination)
            return (DeviceExportScanState.Blocked, "export-registration-changed");
        return ManifestRevision(row["ManifestKey"].AsString) != checkpoint.ManifestRevision
            ? (DeviceExportScanState.Superseded, "export-manifest-changed") : null;
    }

    private bool HasLeasedScan(string scope, DateTime utc) => database.GetCollection<BsonDocument>(ExportScanCollection)
        .Find(Query.EQ("ManifestKey", scope)).Any(row => LeaseActive(row, utc));

    private static bool LeaseActive(BsonDocument row, DateTime utc) => row["LeaseUntilUtcTicks"].IsInt64 && row["LeaseUntilUtcTicks"].AsInt64 > utc.Ticks;

    private static bool MatchesLease(BsonDocument row, DeviceExportScanLease lease, DateTime utc) => row["LeaseId"].IsGuid &&
        row["LeaseId"].AsGuid == lease.LeaseId && row["Revision"].AsInt64 == lease.Checkpoint.Revision && LeaseActive(row, utc);

    private static void UpdateScan(ILiteCollection<BsonDocument> table, BsonDocument row, DeviceExportScanState state, string? failureCode, DateTime due)
    {
        row["State"] = state.ToString();
        row["FailureCode"] = failureCode is null ? BsonValue.Null : new BsonValue(failureCode);
        row["DueUtcTicks"] = due.Ticks;
        row["Revision"] = checked(row["Revision"].AsInt64 + 1);
        row.Remove("LeaseId");
        row.Remove("LeaseUntilUtcTicks");
        table.Update(row);
    }

    /// <summary>Reads a checkpoint under its original scope and applies a compatible paired receiver root.</summary>
    /// <remarks>2026-11-24: Recheck older transfer destinations for FR-FILESYNC-001#ac-safety.</remarks>
    private DeviceExportScanCheckpoint ReadScan(BsonDocument row) => new(row["_id"].AsGuid, row["PlanId"].AsGuid, row["PlanRevision"].AsInt64,
        row["ManifestRevision"].AsInt64, row["Revision"].AsInt64, ReadStoredExportTarget(row["Destination"]),
        Enum.Parse<DeviceExportScanState>(row["State"].AsString), row["Attempts"].AsInt32, Text(row, "FailureCode"),
        row["BytesVerified"].IsInt64 ? row["BytesVerified"].AsInt64 : 0, row["TotalBytes"].IsInt64 ? row["TotalBytes"].AsInt64 : null);

    private static void EnsureExportScanIndexes(ILiteDatabase database)
    {
        var table = database.GetCollection<BsonDocument>(ExportScanCollection);
        table.EnsureIndex("ManifestKey");
        table.EnsureIndex("State");
        database.GetCollection<BsonDocument>(ExportScanItems).EnsureIndex("TransferId");
    }
}
