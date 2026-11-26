using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the native configuration profile on the same revision as library setup.</summary>
/// <remarks>2026-11-25: Commit the typed settings and original operation receipt atomically for FR-BEETS-001#ac-open.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string BeetsConfigurationReceiptType = "SaveHostBeetsConfiguration";

    /// <inheritdoc />
    Task<HostOutcome<HostBeetsConfiguration?>> IHostBeetsSetupStore.CheckBeetsConfigurationAsync(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, CancellationToken token) =>
        OnLaneAsync(() => CheckBeetsConfiguration(library, caller, request, BeetsSetupRow(library), ConfigurationFingerprint(library, request)), token);

    /// <inheritdoc />
    Task<HostOutcome<HostBeetsConfiguration>> IHostBeetsSetupStore.CommitBeetsConfigurationAsync(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, ImmutableArray<string> availablePluginIds, CancellationToken token) =>
        ExecuteAsync<HostBeetsConfiguration>(_ =>
        {
            var row = BeetsSetupRow(library);
            var fingerprint = ConfigurationFingerprint(library, request);
            var check = CheckBeetsConfiguration(library, caller, request, row, fingerprint);
            if (check is not HostOutcome<HostBeetsConfiguration?>.Success { Value: var previous })
                return check.PropagateFailure<HostBeetsConfiguration>();
            if (previous is not null)
                return new HostOutcome<HostBeetsConfiguration>.Success(previous);
            var result = new HostBeetsConfiguration(library, checked(request.ExpectedRevision + 1), request.Settings, true)
            {
                AvailablePluginIds = availablePluginIds, OperationId = request.OperationId
            };
            row["Revision"] = result.Revision;
            row["Configuration"] = JsonSerializer.Serialize(request.Settings);
            database.GetCollection<BsonDocument>(BeetsSetupTable).Upsert(row);
            database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
            {
                ["_id"] = OperationId(new(caller.InstanceId, request.OperationId)), ["OriginInstanceId"] = caller.InstanceId,
                ["OperationId"] = request.OperationId, ["IntentFingerprint"] = fingerprint,
                ["Receipt"] = new BsonDocument { ["Type"] = BeetsConfigurationReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<HostBeetsConfiguration>.Success(result);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<BeetsNativeConfiguration>> IHostBeetsSetupStore.ReadBeetsNativeConfigurationAsync(Guid library, CancellationToken token) =>
        OnLaneAsync<BeetsNativeConfiguration>(() =>
        {
            var row = BeetsSetupRow(library);
            return new HostOutcome<BeetsNativeConfiguration>.Success(new(row["Revision"].AsInt64, SavedBeetsConfiguration(row)));
        }, token);

    private HostOutcome<HostBeetsConfiguration?> CheckBeetsConfiguration(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, BsonDocument row, string fingerprint)
    {
        if (!CanInitializeBeets(library, caller, request.OriginInstanceId))
            return new HostOutcome<HostBeetsConfiguration?>.Forbidden();
        var operation = OperationId(new(caller.InstanceId, request.OperationId));
        if (database.GetCollection<BsonDocument>("operation_receipts").FindById(operation) is { } prior)
        {
            var record = prior["Receipt"].AsDocument;
            return Text(prior, "IntentFingerprint") == fingerprint && Text(record, "Type") == BeetsConfigurationReceiptType &&
                record["Version"].AsInt32 == 1
                ? new HostOutcome<HostBeetsConfiguration?>.Success(ReadStored<HostBeetsConfiguration>(record["Result"]))
                : BeetsSetupConflict<HostBeetsConfiguration?>("operation-intent-mismatch");
        }
        var revision = row["Revision"].AsInt64;
        if (revision != request.ExpectedRevision)
            return BeetsSetupConflict<HostBeetsConfiguration?>("stale-beets-library-settings", revision);
        if (PendingBeetsSetup(row) is not null)
            return BeetsSetupConflict<HostBeetsConfiguration?>("beets-library-setup-pending", revision);
        return new HostOutcome<HostBeetsConfiguration?>.Success(null);
    }

    private static HostBeetsSettings? SavedBeetsConfiguration(BsonDocument row) =>
        row["Configuration"].IsString ? ReadStored<HostBeetsSettings>(row["Configuration"]) : null;

    private static string ConfigurationFingerprint(Guid library, SaveHostBeetsConfigurationRequest request) =>
        HashKey("beets-configuration/v1/" + JsonSerializer.Serialize(new
        {
            library, request.ExpectedRevision, request.Settings.CatalogPath, request.Settings.ConfigurationPath,
            request.Settings.SourceMusicRoot, request.Settings.NamingTemplate, request.Settings.EnabledPluginIds,
            Behavior = request.Settings.ImportDefaults[BeetsConfigurationValues.Behavior]
        }));
}
