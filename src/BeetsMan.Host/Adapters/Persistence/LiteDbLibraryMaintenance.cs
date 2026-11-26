using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the scoped preview, exact native targets, and replayable library maintenance job.</summary>
/// <remarks>2026-11-24: Keep previews separate from published state for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : ILibraryMaintenanceStore
{
    private const string MaintenancePlans = "library_maintenance_plans";
    private const string MaintenanceJobKind = "LibraryMaintenance";
    private const string MaintenanceReceiptType = "AcceptedLibraryMaintenance";

    /// <inheritdoc />
    Task<HostOutcome<ImmutableArray<MaintenanceTrack>>> ILibraryMaintenanceStore.ReadSelectionAsync(Guid libraryId, AuthenticatedHostInstance caller,
        long catalogRevision, ImmutableArray<Guid> trackIds, CancellationToken token) =>
        OnLaneAsync(() => ReadMaintenanceSelection(libraryId, caller, catalogRevision, trackIds), token);

    /// <inheritdoc />
    Task<HostOutcome<LibraryMaintenancePlan>> ILibraryMaintenanceStore.SavePlanAsync(AuthenticatedHostInstance caller, LibraryMaintenancePlan plan,
        ImmutableArray<MaintenanceTrack> tracks, ImmutableArray<AudioRemovalFile> audioFiles, CancellationToken token) => ExecuteAsync<LibraryMaintenancePlan>(_ =>
    {
        var recheck = RecheckMaintenance(plan, caller, tracks);
        if (recheck is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success)
            return recheck.PropagateFailure<LibraryMaintenancePlan>();
        var row = new BsonDocument
        {
            ["_id"] = plan.PlanId, ["LibraryId"] = plan.LibraryId, ["OriginInstanceId"] = caller.InstanceId,
            ["Plan"] = JsonSerializer.Serialize(plan), ["Targets"] = JsonSerializer.Serialize(tracks)
        };
        if (!audioFiles.IsDefaultOrEmpty)
            row["AudioFiles"] = JsonSerializer.Serialize(audioFiles);
        database.GetCollection<BsonDocument>(MaintenancePlans).Insert(row);
        return new HostOutcome<LibraryMaintenancePlan>.Success(plan);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<LibraryMaintenanceReceipt>> ILibraryMaintenanceStore.ConfirmAsync(Guid libraryId, Guid planId, AuthenticatedHostInstance caller,
        ConfirmLibraryMaintenanceRequest request, CancellationToken token) => ExecuteAsync<LibraryMaintenanceReceipt>(_ =>
    {
        if (!IsImportCaller(libraryId, caller) || request.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<LibraryMaintenanceReceipt>.Forbidden();
        var plans = database.GetCollection<BsonDocument>(MaintenancePlans);
        var row = plans.FindById(planId);
        if (row is null || row["LibraryId"].AsGuid != libraryId || row["OriginInstanceId"].AsGuid != caller.InstanceId)
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("maintenance-plan-unavailable");

        var plan = ReadStored<LibraryMaintenancePlan>(row["Plan"]);
        if (plan.Kind != request.Kind)
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("maintenance-plan-mode-mismatch", Conflict: true);
        var fingerprint = HashKey(string.Create(CultureInfo.InvariantCulture,
            $"library-maintenance/v1/{libraryId:N}/{planId:N}/{request.Kind}/{request.ExpectedCatalogRevision}/{request.AudioDeletionApproved}"));
        var key = OperationId(new(caller.InstanceId, request.OperationId));
        var receipts = database.GetCollection<BsonDocument>("operation_receipts");
        if (receipts.FindById(key) is { } prior)
        {
            var payload = prior["Receipt"].AsDocument;
            return Text(prior, "IntentFingerprint") == fingerprint && Text(payload, "Type") == MaintenanceReceiptType &&
                payload["Version"].IsInt32 && payload["Version"].AsInt32 == 1
                ? new HostOutcome<LibraryMaintenanceReceipt>.Success(ReadStored<LibraryMaintenanceReceipt>(payload["Result"])) :
                new HostOutcome<LibraryMaintenanceReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
        }
        if (request.ExpectedCatalogRevision != plan.CatalogRevision)
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("stale-library-maintenance", ReadFeedHead(database, libraryId)["CurrentRevision"].AsInt64, Conflict: true);
        if (row["JobId"].IsGuid || row["BatchOperationId"].IsGuid)
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("maintenance-plan-already-admitted", Conflict: true);
        if (plan.Kind == LibraryMaintenanceKind.Organize && plan.Moves.IsEmpty)
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("maintenance-plan-has-no-moves");

        var targets = ReadStored<MaintenanceTrack[]>(row["Targets"]).ToImmutableArray();
        if (plan.Kind == LibraryMaintenanceKind.RemoveAudio && !AudioRemoval.ManifestMatches(targets, ReadAudioRemovalFiles(row)))
            return new HostOutcome<LibraryMaintenanceReceipt>.Rejected("audio-removal-preview-required", Conflict: true);
        var current = RecheckMaintenance(plan, caller, targets);
        if (current is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success)
            return current.PropagateFailure<LibraryMaintenanceReceipt>();

        var result = new LibraryMaintenanceReceipt(request.OperationId, Guid.NewGuid(), libraryId, 1);
        database.GetCollection<BsonDocument>(LibraryJobs).Insert(new BsonDocument
        {
            ["_id"] = result.JobId, ["LibraryId"] = libraryId, ["OriginInstanceId"] = caller.InstanceId,
            ["OperationId"] = request.OperationId, ["Kind"] = MaintenanceJobKind, ["Status"] = nameof(ImportJobPhase.Accepted),
            ["Revision"] = result.Revision, ["MaintenancePlanId"] = planId, ["MaintenanceMode"] = plan.Kind.ToString(),
            ["ExpectedRevision"] = plan.CatalogRevision, ["AudioDeletionApproved"] = request.AudioDeletionApproved
        });
        row["JobId"] = result.JobId;
        plans.Update(row);
        receipts.Insert(new BsonDocument
        {
            ["_id"] = key, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId, ["IntentFingerprint"] = fingerprint,
            ["Receipt"] = new BsonDocument { ["Type"] = MaintenanceReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
        });
        return new HostOutcome<LibraryMaintenanceReceipt>.Success(result);
    }, token);

    /// <summary>Loads the immutable audio fingerprint evidence from an approved preview.</summary>
    private static ImmutableArray<AudioRemovalFile> ReadAudioRemovalFiles(BsonDocument row) => row["AudioFiles"].IsString
        ? [.. ReadStored<AudioRemovalFile[]>(row["AudioFiles"])] : [];

    private HostOutcome<ImmutableArray<MaintenanceTrack>> RecheckMaintenance(LibraryMaintenancePlan plan, AuthenticatedHostInstance caller,
        ImmutableArray<MaintenanceTrack> expected)
    {
        var current = ReadMaintenanceSelection(plan.LibraryId, caller, plan.CatalogRevision, plan.TrackIds);
        return current is HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var observed } && !observed.SequenceEqual(expected)
            ? new HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected("stale-library-maintenance", plan.CatalogRevision, Conflict: true) : current;
    }

    private HostOutcome<ImmutableArray<MaintenanceTrack>> ReadMaintenanceSelection(Guid libraryId, AuthenticatedHostInstance caller,
        long catalogRevision, ImmutableArray<Guid> trackIds)
    {
        if (!IsImportCaller(libraryId, caller))
            return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Forbidden();
        var actual = ReadFeedHead(database, libraryId)["CurrentRevision"].AsInt64;
        if (actual != catalogRevision)
            return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected("stale-library-maintenance", actual, Conflict: true);

        return ReadMaintenanceTracks(libraryId, trackIds);
    }

    /// <summary>Reads the current scoped targets independently of the feed head for FR-BEETS-001#ac-recover.</summary>
    private HostOutcome<ImmutableArray<MaintenanceTrack>> ReadMaintenanceTracks(Guid libraryId, ImmutableArray<Guid> trackIds)
    {
        var targets = ImmutableArray.CreateBuilder<MaintenanceTrack>(trackIds.Length);
        var paths = new HashSet<string>(ImportCatalogPublication.PathComparer);
        var nativeIds = new HashSet<long>();
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        foreach (var id in trackIds)
        {
            var row = CatalogRow(tracks, id, libraryId);
            if (row is null)
                return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected("library-track-unavailable");
            if (Text(row, "RelativePath") is not { } path || !HostLibraryMaintenance.IsLibraryRelativePath(path) ||
                !row["Revision"].IsNumber || row["Revision"].AsInt64 <= 0 || !row["BeetsTrackId"].IsNumber || row["BeetsTrackId"].AsInt64 <= 0 ||
                !paths.Add(path) || !nativeIds.Add(row["BeetsTrackId"].AsInt64))
                return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected("maintenance-native-track-unavailable");
            targets.Add(new(id, row["BeetsTrackId"].AsInt64, row["Revision"].AsInt64, path));
        }
        return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Success(targets.MoveToImmutable());
    }
}
