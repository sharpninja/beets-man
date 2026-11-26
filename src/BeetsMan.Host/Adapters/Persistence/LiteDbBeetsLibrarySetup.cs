using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Stores the single native library setup intent and its original committed result.</summary>
/// <remarks>2026-11-25: Persist each native setup checkpoint before the next file effect for FR-BEETS-001#ac-open and ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IHostBeetsSetupStore
{
    private const string BeetsSetupTable = "beets_library_setup";
    private const string BeetsSetupReceiptType = "InitializeHostBeets";

    /// <inheritdoc />
    Task<HostOutcome<BeetsSetupRead>> IHostBeetsSetupStore.ReadBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        CancellationToken token) => OnLaneAsync<BeetsSetupRead>(() =>
    {
        if (!IsImportCaller(library, caller))
            return new HostOutcome<BeetsSetupRead>.Forbidden();
        var row = BeetsSetupRow(library);
        return new HostOutcome<BeetsSetupRead>.Success(new(row["Revision"].AsInt64, caller.CanManageRegistrations,
            PendingBeetsSetup(row), SavedBeetsConfiguration(row)));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<BeetsSetupAdmission>> IHostBeetsSetupStore.ReserveBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        InitializeHostBeetsRequest request, bool destinationExists, CancellationToken token) => ExecuteAsync<BeetsSetupAdmission>(_ =>
    {
        if (!CanInitializeBeets(library, caller, request.OriginInstanceId))
            return new HostOutcome<BeetsSetupAdmission>.Forbidden();
        var intent = HashKey(JsonSerializer.Serialize(new
        {
            library, request.ExpectedRevision, request.Settings.CatalogPath, request.Settings.ConfigurationPath,
            request.Settings.SourceMusicRoot, request.Settings.NamingTemplate, request.Settings.EnabledPluginIds,
            Defaults = request.Settings.ImportDefaults.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray()
        }));
        var operation = OperationId(new(caller.InstanceId, request.OperationId));
        if (database.GetCollection<BsonDocument>("operation_receipts").FindById(operation) is { } saved)
        {
            var receipt = saved["Receipt"].AsDocument;
            return Text(saved, "IntentFingerprint") == intent && Text(receipt, "Type") == BeetsSetupReceiptType && receipt["Version"].AsInt32 == 1
                ? new HostOutcome<BeetsSetupAdmission>.Success(new(null, ReadStored<HostBeetsLibraryState>(receipt["Result"])))
                : BeetsSetupConflict<BeetsSetupAdmission>("operation-intent-mismatch");
        }
        var row = BeetsSetupRow(library);
        var revision = row["Revision"].AsInt64;
        if (request.ExpectedRevision != revision)
            return BeetsSetupConflict<BeetsSetupAdmission>("stale-beets-library-settings", revision);
        if (PendingBeetsSetup(row) is { } pending)
            return pending.Origin == caller.InstanceId && pending.Operation == request.OperationId && pending.Fingerprint == intent
                ? new HostOutcome<BeetsSetupAdmission>.Success(new(pending, null))
                : BeetsSetupConflict<BeetsSetupAdmission>("beets-library-setup-pending", revision);
        if (destinationExists || revision > 0)
            return BeetsSetupConflict<BeetsSetupAdmission>("beets-library-already-initialized", revision);

        var lease = new BeetsSetupLease(caller.InstanceId, request.OperationId, intent, null);
        row["Pending"] = JsonSerializer.Serialize(lease);
        database.GetCollection<BsonDocument>(BeetsSetupTable).Upsert(row);
        return new HostOutcome<BeetsSetupAdmission>.Success(new(lease, null));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<BeetsSetupLease>> IHostBeetsSetupStore.PinBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        BeetsSetupLease lease, PreparedBeetsLibrary files, CancellationToken token) => ExecuteAsync<BeetsSetupLease>(_ =>
    {
        if (!CanInitializeBeets(library, caller, lease.Origin))
            return new HostOutcome<BeetsSetupLease>.Forbidden();
        var row = BeetsSetupRow(library);
        if (PendingBeetsSetup(row) != lease)
            return BeetsSetupConflict<BeetsSetupLease>("beets-library-setup-intent-changed", row["Revision"].AsInt64);
        var pinned = lease with { Prepared = files };
        row["Pending"] = JsonSerializer.Serialize(pinned);
        database.GetCollection<BsonDocument>(BeetsSetupTable).Update(row);
        return new HostOutcome<BeetsSetupLease>.Success(pinned);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<HostBeetsLibraryState>> IHostBeetsSetupStore.CompleteBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        BeetsSetupLease lease, CancellationToken token) => ExecuteAsync<HostBeetsLibraryState>(_ =>
    {
        if (!CanInitializeBeets(library, caller, lease.Origin))
            return new HostOutcome<HostBeetsLibraryState>.Forbidden();
        var row = BeetsSetupRow(library);
        var revision = row["Revision"].AsInt64;
        if (PendingBeetsSetup(row) != lease || lease.Prepared is null)
            return BeetsSetupConflict<HostBeetsLibraryState>("beets-library-setup-intent-changed", revision);
        var receipt = new HostBeetsLibraryState(library, checked(revision + 1), true, true, lease.Operation);
        row["Revision"] = receipt.Revision;
        row.Remove("Pending");
        database.GetCollection<BsonDocument>(BeetsSetupTable).Update(row);
        database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
        {
            ["_id"] = OperationId(new(lease.Origin, lease.Operation)), ["OriginInstanceId"] = lease.Origin, ["OperationId"] = lease.Operation,
            ["IntentFingerprint"] = lease.Fingerprint,
            ["Receipt"] = new BsonDocument { ["Type"] = BeetsSetupReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(receipt) }
        });
        return new HostOutcome<HostBeetsLibraryState>.Success(receipt);
    }, token);

    private bool CanInitializeBeets(Guid library, AuthenticatedHostInstance caller, Guid origin) =>
        caller.CanManageRegistrations && caller.InstanceId == origin && IsImportCaller(library, caller);

    private BsonDocument BeetsSetupRow(Guid library) => database.GetCollection<BsonDocument>(BeetsSetupTable).FindById(library) ??
        new BsonDocument { ["_id"] = library, ["Revision"] = 0L };

    private static BeetsSetupLease? PendingBeetsSetup(BsonDocument row) =>
        row["Pending"].IsString ? ReadStored<BeetsSetupLease>(row["Pending"]) : null;

    private static HostOutcome<T> BeetsSetupConflict<T>(string issue, long? revision = null) =>
        new HostOutcome<T>.Rejected(issue, revision, Conflict: true);
}
