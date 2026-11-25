using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Captures the current plan and one authoritative catalog snapshot before reading the receiver.</summary>
/// <remarks>2026-11-24: Recheck STB10 staged writes before committing them for FR-FILESYNC-001#ac-safety.</remarks>
public abstract record DeviceTrackStagePreparation
{
    /// <summary>Identifies the original result of the same operation.</summary>
    public sealed record Replay(DeviceSyncListEditResponse Response) : DeviceTrackStagePreparation;

    /// <summary>Provides the inputs for an as-yet-uncommitted request.</summary>
    public sealed record Pending(DeviceExportPlan? Plan, DevicePlaylistSources Sources) : DeviceTrackStagePreparation;
}

/// <summary>Resolves exact track paths and availability under one profile and registered share.</summary>
public interface IHostDirectTrackResolver
{
    /// <summary>Reads receiver inventory for the already checked catalog inputs.</summary>
    Task<HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>> ResolveTracksAsync(DevicePlaylistSources sources,
        StageDeviceTracksRequest request, DeviceExportPlan? plan, CancellationToken token);
}

/// <summary>Checks and commits staged track additions and removals with operation receipts.</summary>
public interface IHostDeviceSyncListStore
{
    /// <summary>Reads an exact scoped plan and catalog snapshot, or a verified replay.</summary>
    Task<HostOutcome<DeviceTrackStagePreparation>> PrepareTracksAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, string fingerprint, CancellationToken token);

    /// <summary>Commits direct additions only if their plan, registration and catalog inputs still match.</summary>
    Task<HostOutcome<DeviceSyncListEditResponse>> CommitTracksAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, string fingerprint, DevicePlaylistSources snapshot, DeviceExportPlan plan, CancellationToken token);

    /// <summary>Removes staged intent and recalculates dependencies within one transaction.</summary>
    Task<HostOutcome<DeviceSyncListEditResponse>> RemoveAsync(Guid libraryId, AuthenticatedHostInstance caller,
        RemoveDeviceExportEntriesRequest request, string fingerprint, CancellationToken token);
}

/// <summary>Applies STB10 direct track drops and staged removals without publishing any file changes.</summary>
/// <remarks>2026-11-24: Bind the exact plan revision and dependency edits for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed class HostDeviceSyncListEditor(IHostDeviceSyncListStore store, IHostDirectTrackResolver resolver)
{
    /// <summary>Stages the selected audio at its actual destination for the chosen app.</summary>
    public async Task<HostOutcome<DeviceSyncListEditResponse>> StageTracksAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDeviceTracksRequest request, CancellationToken token)
    {
        if (!Authorizes(libraryId, caller, request.OriginInstanceId))
            return new HostOutcome<DeviceSyncListEditResponse>.Forbidden();
        if (request.OperationId == Guid.Empty || request.DeviceId == Guid.Empty || !ValidSelection(request.OrderedTrackIds) ||
            string.IsNullOrWhiteSpace(request.ProfileId) || request.ProfileRevision <= 0 ||
            (request.PlanId is { } id ? id == Guid.Empty || !ValidRevision(request.ExpectedPlanRevision) : request.ExpectedPlanRevision.HasValue))
            return Rejected("invalid-export-target");

        try
        {
            var fingerprint = Fingerprint(libraryId, "stage-tracks", request);
            var prepared = await store.PrepareTracksAsync(libraryId, caller, request, fingerprint, token).ConfigureAwait(false);
            if (prepared is not HostOutcome<DeviceTrackStagePreparation>.Success { Value: var stage })
                return prepared.PropagateFailure<DeviceSyncListEditResponse>();
            if (stage is DeviceTrackStagePreparation.Replay replay)
                return new HostOutcome<DeviceSyncListEditResponse>.Success(replay.Response);

            var pending = (DeviceTrackStagePreparation.Pending)stage;
            var resolved = await resolver.ResolveTracksAsync(pending.Sources, request, pending.Plan, token).ConfigureAwait(false);
            if (resolved is not HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>.Success { Value: var tracks })
                return resolved.PropagateFailure<DeviceSyncListEditResponse>();
            if (tracks.IsDefault || !request.OrderedTrackIds.Distinct().SequenceEqual(tracks.Select(track => track.TrackId)))
                return Rejected("invalid-export-inventory");
            var planned = DeviceSyncListPlan.Stage(request, pending.Plan, pending.Sources.Destination.CaseSensitivePaths, tracks);
            if (planned is not HostOutcome<DeviceExportPlan>.Success { Value: var plan })
                return planned.PropagateFailure<DeviceSyncListEditResponse>();
            return await store.CommitTracksAsync(libraryId, caller, request, fingerprint, pending.Sources, plan, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<DeviceSyncListEditResponse>.Cancelled();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<DeviceSyncListEditResponse>.Unavailable("export-staging-unavailable");
        }
    }

    /// <summary>Prunes the selected staged intent and returns the persisted plan revision.</summary>
    public async Task<HostOutcome<DeviceSyncListEditResponse>> RemoveAsync(Guid libraryId, AuthenticatedHostInstance caller,
        RemoveDeviceExportEntriesRequest request, CancellationToken token)
    {
        if (!Authorizes(libraryId, caller, request.OriginInstanceId))
            return new HostOutcome<DeviceSyncListEditResponse>.Forbidden();
        if (request.OperationId == Guid.Empty || request.PlanId == Guid.Empty || !ValidRevision(request.ExpectedPlanRevision) ||
            !ValidSelection(request.EntryIds))
            return Rejected("invalid-export-removal-intent");
        return await store.RemoveAsync(libraryId, caller, request, Fingerprint(libraryId, "remove-staged", request), token).ConfigureAwait(false);
    }

    private static bool Authorizes(Guid libraryId, AuthenticatedHostInstance caller, Guid origin) =>
        libraryId != Guid.Empty && caller.LibraryId == libraryId && origin != Guid.Empty && caller.InstanceId == origin;

    private static bool ValidRevision(long? revision) => revision is > 0 and < long.MaxValue;

    private static bool ValidSelection(ImmutableArray<Guid> ids) => !ids.IsDefaultOrEmpty && ids.All(id => id != Guid.Empty);

    private static string Fingerprint<T>(Guid libraryId, string operation, T request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, LibraryId = libraryId, Operation = operation, Request = request })));

    private static HostOutcome<DeviceSyncListEditResponse> Rejected(string code) => new HostOutcome<DeviceSyncListEditResponse>.Rejected(code);
}
