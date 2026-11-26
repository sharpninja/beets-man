using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Pins one selected catalog track to its native item, paths, and revision.</summary>
/// <remarks>2026-11-24: Revalidate exact effects for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record MaintenanceTrack(Guid TrackId, long BeetsTrackId, long TrackRevision, string RelativePath);

/// <summary>Previews configured beets organization and verifies the exact paths of selected audio.</summary>
internal interface IBeetsMaintenancePlanner
{
    Task<HostOutcome<ImmutableArray<MaintenancePathChange>>> PreviewMovesAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token);
    Task<HostOutcome<ImmutableArray<AudioRemovalFile>>> PreviewAudioRemovalAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token);
}

/// <summary>Persists private approval plans, and admits only an exact authorized plan.</summary>
internal interface ILibraryMaintenanceStore
{
    Task<HostOutcome<ImmutableArray<MaintenanceTrack>>> ReadSelectionAsync(Guid libraryId, AuthenticatedHostInstance caller,
        long catalogRevision, ImmutableArray<Guid> trackIds, CancellationToken token);
    Task<HostOutcome<LibraryMaintenancePlan>> SavePlanAsync(AuthenticatedHostInstance caller, LibraryMaintenancePlan plan,
        ImmutableArray<MaintenanceTrack> tracks, ImmutableArray<AudioRemovalFile> audioFiles, CancellationToken token);
    Task<HostOutcome<LibraryMaintenanceReceipt>> ConfirmAsync(Guid libraryId, Guid planId, AuthenticatedHostInstance caller,
        ConfirmLibraryMaintenanceRequest request, CancellationToken token);
}

/// <summary>Separates read-only maintenance proposals from admitted library jobs.</summary>
/// <remarks>2026-11-24: Protect STB18 approval and removal scope for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed class HostLibraryMaintenance(ILibraryMaintenanceStore store, IBeetsMaintenancePlanner? native)
{
    /// <summary>Captures the exact paths and catalog extent for later confirmation.</summary>
    public async Task<HostOutcome<LibraryMaintenancePlan>> PreviewAsync(Guid libraryId, AuthenticatedHostInstance caller,
        LibraryMaintenancePreviewRequest request, CancellationToken token)
    {
        if (!ValidKind(request.Kind) || request.CatalogRevision <= 0 || request.TrackIds.IsDefaultOrEmpty ||
            request.TrackIds.Any(id => id == Guid.Empty) || request.TrackIds.Distinct().Count() != request.TrackIds.Length)
            return new HostOutcome<LibraryMaintenancePlan>.Rejected("invalid-library-maintenance-selection");

        var selected = await store.ReadSelectionAsync(libraryId, caller, request.CatalogRevision, request.TrackIds, token).ConfigureAwait(false);
        if (selected is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var tracks })
            return selected.PropagateFailure<LibraryMaintenancePlan>();
        var plan = new LibraryMaintenancePlan(Guid.NewGuid(), libraryId, request.Kind, request.CatalogRevision, request.TrackIds, [], []);
        ImmutableArray<AudioRemovalFile> audioFiles = [];
        switch (request.Kind)
        {
            case LibraryMaintenanceKind.Organize:
                if (native is null)
                    return new HostOutcome<LibraryMaintenancePlan>.Unavailable("beets-maintenance-unavailable");
                var moves = await native.PreviewMovesAsync(tracks, token).ConfigureAwait(false);
                if (moves is not HostOutcome<ImmutableArray<MaintenancePathChange>>.Success { Value: var paths })
                    return moves.PropagateFailure<LibraryMaintenancePlan>();
                if (!ValidMoves(tracks, paths))
                    return new HostOutcome<LibraryMaintenancePlan>.Rejected("invalid-library-maintenance-paths");
                plan = plan with { Moves = paths };
                break;
            case LibraryMaintenanceKind.RemoveAudio:
                if (native is null)
                    return new HostOutcome<LibraryMaintenancePlan>.Unavailable("beets-maintenance-unavailable");
                var audio = await native.PreviewAudioRemovalAsync(tracks, token).ConfigureAwait(false);
                if (audio is not HostOutcome<ImmutableArray<AudioRemovalFile>>.Success { Value: var deleted })
                    return audio.PropagateFailure<LibraryMaintenancePlan>();
                if (!AudioRemoval.ManifestMatches(tracks, deleted))
                    return new HostOutcome<LibraryMaintenancePlan>.Rejected("invalid-library-maintenance-paths");
                audioFiles = deleted;
                plan = plan with { AudioPaths = [.. deleted.Select(file => file.RelativePath)] };
                break;
        }
        return await store.SavePlanAsync(caller, plan, tracks, audioFiles, token).ConfigureAwait(false);
    }

    /// <summary>Admits the exact preview plus explicit audio approval, if requested.</summary>
    public Task<HostOutcome<LibraryMaintenanceReceipt>> ConfirmAsync(Guid libraryId, Guid planId, AuthenticatedHostInstance caller,
        ConfirmLibraryMaintenanceRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<LibraryMaintenanceReceipt>>(new HostOutcome<LibraryMaintenanceReceipt>.Forbidden());
        if (planId == Guid.Empty || request.OperationId == Guid.Empty || request.ExpectedCatalogRevision <= 0 || !ValidKind(request.Kind))
            return Task.FromResult<HostOutcome<LibraryMaintenanceReceipt>>(new HostOutcome<LibraryMaintenanceReceipt>.Rejected("invalid-library-maintenance-approval"));
        if ((request.Kind == LibraryMaintenanceKind.RemoveAudio) != request.AudioDeletionApproved)
            return Task.FromResult<HostOutcome<LibraryMaintenanceReceipt>>(new HostOutcome<LibraryMaintenanceReceipt>.Rejected(
                request.Kind == LibraryMaintenanceKind.RemoveAudio ? "audio-deletion-approval-required" : "invalid-library-maintenance-approval"));
        return store.ConfirmAsync(libraryId, planId, caller, request, token);
    }

    private static bool ValidKind(LibraryMaintenanceKind kind) => kind is LibraryMaintenanceKind.Organize or LibraryMaintenanceKind.RemoveCatalog or LibraryMaintenanceKind.RemoveAudio;

    /// <summary>Validates reviewed organization paths during preview and durable recovery for FR-BEETS-001#ac-edit and ac-recover.</summary>
    internal static bool ValidMoves(ImmutableArray<MaintenanceTrack> tracks, ImmutableArray<MaintenancePathChange> moves)
    {
        if (moves.IsDefault)
            return false;
        var comparer = ImportCatalogPublication.PathComparer;
        var sources = tracks.ToDictionary(track => track.TrackId, track => track.RelativePath);
        var selectedPaths = sources.Values.ToHashSet(comparer);
        return moves.Select(move => move.TrackId).Distinct().Count() == moves.Length &&
            moves.Select(move => move.Destination).Distinct(comparer).Count() == moves.Length &&
            moves.All(move => sources.TryGetValue(move.TrackId, out var path) && comparer.Equals(path, move.Source) &&
                IsLibraryRelativePath(move.Destination) && !selectedPaths.Contains(move.Destination));
    }

    /// <summary>Recognizes an ordinary relative path under the configured music tree.</summary>
    internal static bool IsLibraryRelativePath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) && path.Split('/').All(segment => segment is not ("" or "." or ".."));
}
