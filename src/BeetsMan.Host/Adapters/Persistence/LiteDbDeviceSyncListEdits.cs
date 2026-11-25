using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists STB10 draft mutations separately from the published device share and its ownership manifest.</summary>
/// <remarks>2026-11-24: Revalidate the registered plan and return durable receipts for FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string SyncListReceiptType = "DeviceSyncListEdited";

    /// <inheritdoc />
    public Task<HostOutcome<DeviceTrackStagePreparation>> PrepareTracksAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, string fingerprint, CancellationToken token) =>
        OnLaneAsync(() => ReadTrackStage(libraryId, caller, request, fingerprint, token), token);

    /// <inheritdoc />
    public Task<HostOutcome<DeviceSyncListEditResponse>> CommitTracksAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, string fingerprint, DevicePlaylistSources snapshot, DeviceExportPlan plan, CancellationToken token) =>
        ExecuteAsync<DeviceSyncListEditResponse>(_ =>
        {
            var prepared = ReadTrackStage(libraryId, caller, request, fingerprint, token);
            if (prepared is not HostOutcome<DeviceTrackStagePreparation>.Success { Value: var stage })
                return prepared.PropagateFailure<DeviceSyncListEditResponse>();
            if (stage is DeviceTrackStagePreparation.Replay replay)
                return new HostOutcome<DeviceSyncListEditResponse>.Success(replay.Response);
            var current = ((DeviceTrackStagePreparation.Pending)stage).Sources;
            if (current.Destination != snapshot.Destination)
                return new HostOutcome<DeviceSyncListEditResponse>.Rejected("export-registration-changed", current.Destination.Revision, Conflict: true);
            if (current.Tracks.Count != snapshot.Tracks.Count ||
                current.Tracks.Any(pair => snapshot.Tracks.GetValueOrDefault(pair.Key) != pair.Value))
                return new HostOutcome<DeviceSyncListEditResponse>.Rejected("export-catalog-changed", Conflict: true);
            return SaveSyncListEdit(caller, request.OperationId, fingerprint, current.Destination, plan);
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<DeviceSyncListEditResponse>> RemoveAsync(Guid libraryId, AuthenticatedHostInstance caller,
        RemoveDeviceExportEntriesRequest request, string fingerprint, CancellationToken token) => ExecuteAsync<DeviceSyncListEditResponse>(_ =>
        {
            var saved = ReadSyncListReceipt(libraryId, caller, request.OperationId, fingerprint);
            if (saved is not HostOutcome<DeviceSyncListEditResponse?>.Success { Value: var replay })
                return saved.PropagateFailure<DeviceSyncListEditResponse>();
            if (replay is not null)
                return new HostOutcome<DeviceSyncListEditResponse>.Success(replay);
            var state = ReadPreviewPlan(libraryId, caller, request.PlanId, request.ExpectedPlanRevision);
            if (state is not HostOutcome<(BsonDocument Row, RegisteredAndroidExport Target)>.Success { Value: var current })
                return state.PropagateFailure<DeviceSyncListEditResponse>();
            if (HasPendingPublication(current.Target))
                return new HostOutcome<DeviceSyncListEditResponse>.Rejected("export-publication-in-progress", Conflict: true);

            var edited = DeviceSyncListPlan.Remove(ReadPlan(current.Row), request.EntryIds, current.Target.CaseSensitivePaths);
            return edited is HostOutcome<DeviceExportPlan>.Success { Value: var plan }
                ? SaveSyncListEdit(caller, request.OperationId, fingerprint, current.Target, plan)
                : edited.PropagateFailure<DeviceSyncListEditResponse>();
        }, token);

    private HostOutcome<DeviceTrackStagePreparation> ReadTrackStage(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, string fingerprint, CancellationToken token)
    {
        var saved = ReadSyncListReceipt(libraryId, caller, request.OperationId, fingerprint);
        if (saved is not HostOutcome<DeviceSyncListEditResponse?>.Success { Value: var replay })
            return saved.PropagateFailure<DeviceTrackStagePreparation>();
        if (replay is not null)
            return new HostOutcome<DeviceTrackStagePreparation>.Success(new DeviceTrackStagePreparation.Replay(replay));

        var authorized = ReadAuthorizedExport(libraryId, caller, request.DeviceId);
        if (authorized is not HostOutcome<RegisteredAndroidExport>.Success { Value: var target })
            return authorized.PropagateFailure<DeviceTrackStagePreparation>();
        if (HasPendingPublication(target))
            return new HostOutcome<DeviceTrackStagePreparation>.Rejected("export-publication-in-progress", Conflict: true);
        var row = FindPlanRow(libraryId, request.DeviceId, request.ProfileId);
        var revision = row?["ManifestRevision"].AsInt64;
        if (row?["_id"].AsGuid != request.PlanId || revision != request.ExpectedPlanRevision ||
            row is not null && row["ProfileVersion"].AsInt64 != request.ProfileRevision)
            return new HostOutcome<DeviceTrackStagePreparation>.Rejected("export-plan-changed", revision, Conflict: true);
        if (row is not null && (row["ShareId"].AsString != target.ShareId || row["CaseSensitivePaths"].AsBoolean != target.CaseSensitivePaths))
            return new HostOutcome<DeviceTrackStagePreparation>.Rejected("export-share-changed", revision, Conflict: true);

        var inputs = ReadExportSources(libraryId, request.DeviceId, request.OrderedTrackIds, token);
        return inputs is HostOutcome<DevicePlaylistSources>.Success { Value: var sources }
            ? new HostOutcome<DeviceTrackStagePreparation>.Success(new DeviceTrackStagePreparation.Pending(row is null ? null : ReadPlan(row), sources))
            : inputs.PropagateFailure<DeviceTrackStagePreparation>();
    }

    private HostOutcome<DeviceSyncListEditResponse?> ReadSyncListReceipt(Guid libraryId, AuthenticatedHostInstance caller, Guid operation, string fingerprint)
    {
        if (!IsPublicationCaller(libraryId, caller))
            return new HostOutcome<DeviceSyncListEditResponse?>.Forbidden();
        var row = database.GetCollection<BsonDocument>("operation_receipts").FindById(OperationId(new(caller.InstanceId, operation)));
        if (row is null)
            return new HostOutcome<DeviceSyncListEditResponse?>.Success(null);
        var receipt = row["Receipt"].AsDocument;
        if (row["IntentFingerprint"].AsString != fingerprint || receipt["Type"].AsString != SyncListReceiptType || receipt["Version"].AsInt32 != 1)
            return new HostOutcome<DeviceSyncListEditResponse?>.Rejected("operation-intent-mismatch", Conflict: true);
        var response = JsonSerializer.Deserialize<DeviceSyncListEditResponse>(receipt["Plan"].AsString);
        return response is not null ? new HostOutcome<DeviceSyncListEditResponse?>.Success(response) :
            new HostOutcome<DeviceSyncListEditResponse?>.Unavailable("export-staging-receipt-invalid");
    }

    private HostOutcome<DeviceSyncListEditResponse> SaveSyncListEdit(AuthenticatedHostInstance caller, Guid operation, string fingerprint,
        RegisteredAndroidExport target, DeviceExportPlan plan)
    {
        var digests = ReadPlaylistDigests(plan.PlanId!.Value);
        WriteStagedPlan(plan, target, [.. plan.Entries.Select(entry => new ReviewedDeviceExportEntry(entry, digests.GetValueOrDefault(entry.EntryId)))]);

        var response = new DeviceSyncListEditResponse(operation, plan);
        database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
        {
            ["_id"] = OperationId(new(caller.InstanceId, operation)), ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = operation,
            ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument
            {
                ["Type"] = SyncListReceiptType, ["Version"] = 1, ["Plan"] = JsonSerializer.Serialize(response)
            }
        });
        return new HostOutcome<DeviceSyncListEditResponse>.Success(response);
    }
}
