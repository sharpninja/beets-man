using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Resolves registered player paths and exact destination availability for the saved playlist.</summary>
/// <remarks>2026-11-24: Separate device inventory from the stage transaction for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public interface IHostDevicePlaylistResolver
{
    /// <summary>Returns an authoritative export input scoped to the selected library, device, and profile.</summary>
    Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token);
}

/// <summary>Pairs an export input with the registered Android share that owns its destinations.</summary>
/// <remarks>2026-11-24: Carry the checked receiver registration into the commit for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record ResolvedDevicePlaylist(RegisteredAndroidExport Destination, ResolvedPlaylistExport Export);

/// <summary>Stores generated playlist bytes privately, before the host commits the staging metadata.</summary>
public interface IStagedPlaylistContent
{
    /// <summary>Writes immutable content and returns its stable content identity.</summary>
    Task<string> StoreAsync(ImmutableArray<byte> content, CancellationToken token);
}

/// <summary>Holds either the current preparation state or the receipt of an identical operation.</summary>
public sealed record DeviceStagePreparation(SavedHostPlaylist? Playlist, DeviceExportPlan? ExistingPlan, StageDevicePlaylistResponse? Replay);

/// <summary>Reads authoritative stage inputs and commits their result if the input revisions still match.</summary>
/// <remarks>2026-11-24: Define the persistence boundary for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public interface IHostDevicePlaylistStore
{
    /// <summary>Checks active registration and replay before resolving device inventory.</summary>
    Task<HostOutcome<DeviceStagePreparation>> PrepareStageAsync(Guid libraryId, StageDevicePlaylistRequest request,
        string fingerprint, CancellationToken token);

    /// <summary>Persists the staged plan, ordered entries, and original receipt in a single transaction.</summary>
    Task<HostOutcome<StageDevicePlaylistResponse>> CommitStageAsync(Guid libraryId, StageDevicePlaylistRequest request,
        string fingerprint, long playlistRevision, ResolvedDevicePlaylist target, PlaylistStageMutation mutation,
        string contentIdentity, CancellationToken token);
}

/// <summary>Stages a saved playlist against the selected Android player's profile and destination inventory.</summary>
/// <remarks>2026-11-24: Bind the authoritative playlist, planner and durable staging for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class HostDevicePlaylistStager(IHostDevicePlaylistStore store, IHostDevicePlaylistResolver resolver,
    PlaylistExportPlanner planner, IStagedPlaylistContent content)
{
    /// <summary>Creates or revises an export plan and returns the original result for a repeated operation.</summary>
    public async Task<HostOutcome<StageDevicePlaylistResponse>> StageAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StageDevicePlaylistRequest request, CancellationToken token)
    {
        if (libraryId == Guid.Empty || caller.LibraryId != libraryId || caller.InstanceId == Guid.Empty || request.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<StageDevicePlaylistResponse>.Forbidden();
        if (request.OperationId == Guid.Empty || request.PlaylistId == Guid.Empty || request.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ProfileId) || request.ProfileRevision <= 0 ||
            (request.PlanId is { } planId ? planId == Guid.Empty || request.ExpectedPlanRevision is not > 0 : request.ExpectedPlanRevision.HasValue))
            return new HostOutcome<StageDevicePlaylistResponse>.Rejected("invalid-export-target");

        try
        {
            var fingerprint = Fingerprint(libraryId, request);
            var prepared = await store.PrepareStageAsync(libraryId, request, fingerprint, token).ConfigureAwait(false);
            if (prepared is not HostOutcome<DeviceStagePreparation>.Success { Value: var stage })
                return prepared.PropagateFailure<StageDevicePlaylistResponse>();
            if (stage.Replay is { } replay)
                return new HostOutcome<StageDevicePlaylistResponse>.Success(replay);

            var playlist = stage.Playlist ?? throw new InvalidOperationException("Stage preparation did not return its saved playlist.");
            var resolved = await resolver.ResolveAsync(playlist, request, token).ConfigureAwait(false);
            if (resolved is not HostOutcome<ResolvedDevicePlaylist>.Success { Value: var target })
                return resolved.PropagateFailure<StageDevicePlaylistResponse>();
            if (target.Destination.LibraryId != libraryId || target.Destination.DeviceId != request.DeviceId ||
                target.Destination.CaseSensitivePaths != target.Export.CaseSensitivePaths || target.Export.Tracks.IsDefault ||
                !playlist.Entries.Select(entry => entry.TrackId).SequenceEqual(target.Export.Tracks.Select(track => track.TrackId)))
                return new HostOutcome<StageDevicePlaylistResponse>.Rejected("invalid-export-inventory");

            var planned = planner.Stage(request, target.Export, stage.ExistingPlan);
            if (planned is not HostOutcome<PlaylistStageMutation>.Success { Value: var mutation })
                return planned.PropagateFailure<StageDevicePlaylistResponse>();
            var identity = await content.StoreAsync(mutation.PlaylistContent, token).ConfigureAwait(false);
            return await store.CommitStageAsync(libraryId, request, fingerprint, playlist.Revision, target, mutation, identity, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<StageDevicePlaylistResponse>.Cancelled();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<StageDevicePlaylistResponse>.Unavailable("export-staging-unavailable");
        }
    }

    private static string Fingerprint(Guid libraryId, StageDevicePlaylistRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Operation = "stage-device-playlist", Version = 1,
            LibraryId = libraryId, Request = request })));
}
