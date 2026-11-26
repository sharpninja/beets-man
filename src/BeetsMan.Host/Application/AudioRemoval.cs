using System.Collections.Immutable;

namespace BeetsMan.Host.Application;

/// <summary>Pins the exact media bytes and relative path reviewed for deletion.</summary>
/// <remarks>2026-11-24: Require the reviewed identity for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record AudioRemovalFile(string RelativePath, long Size, string Sha256)
{
    /// <summary>Checks whether this fingerprint belongs to the pinned maintenance target.</summary>
    internal bool Matches(MaintenanceTrack track) => ImportCatalogPublication.PathComparer.Equals(RelativePath, track.RelativePath) &&
        Size > 0 && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}

/// <summary>Describes the approved file at its pinned path.</summary>
internal enum NativeAudioFileState { Present, Missing, Diverged }

/// <summary>Reads and deletes only the media bytes pinned by an audio-removal preview.</summary>
internal interface IAudioRemovalFiles
{
    Task<HostOutcome<NativeAudioFileState>> ObserveAudioFileAsync(AudioRemovalFile file, CancellationToken token);
    Task<HostOutcome<bool>> DeleteAudioFileAsync(AudioRemovalFile file, CancellationToken token);
}

/// <summary>Reconciles the catalog and file sides of an explicitly authorized audio removal.</summary>
/// <remarks>2026-11-24: Preserve other files and recover an interrupted deletion for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal static class AudioRemoval
{
    /// <summary>Records the outstanding effects for the same pinned track and media file.</summary>
    internal sealed record PendingEffect(MaintenanceTrack Track, AudioRemovalFile File, bool CatalogPresent);

    /// <summary>Preflights every target before any mutation and accepts only recoverable partial states after the fence.</summary>
    internal static async Task<HostOutcome<ImmutableArray<PendingEffect>>> ReadPendingAsync(ImmutableArray<MaintenanceTrack> tracks,
        ImmutableArray<AudioRemovalFile> files, bool attempted, IBeetsCatalogRemoval catalog, IAudioRemovalFiles audio, CancellationToken token,
        IReadOnlyDictionary<Guid, string>? organizedPaths = null)
    {
        if (!ManifestMatches(tracks, files))
            return new HostOutcome<ImmutableArray<PendingEffect>>.Rejected("audio-removal-preview-required", Conflict: true);
        var effects = ImmutableArray.CreateBuilder<PendingEffect>();
        foreach (var (reviewedTrack, file) in tracks.Zip(files))
        {
            var track = organizedPaths?.TryGetValue(reviewedTrack.TrackId, out var path) == true
                ? reviewedTrack with { RelativePath = path } : reviewedTrack;
            var catalogResult = await catalog.ObserveCatalogTrackAsync(track, token).ConfigureAwait(false);
            if (catalogResult is not HostOutcome<NativeCatalogTrackState>.Success { Value: var catalogState })
                return catalogResult.PropagateFailure<ImmutableArray<PendingEffect>>();
            var fileResult = await audio.ObserveAudioFileAsync(file, token).ConfigureAwait(false);
            if (fileResult is not HostOutcome<NativeAudioFileState>.Success { Value: var fileState })
                return fileResult.PropagateFailure<ImmutableArray<PendingEffect>>();
            if (catalogState == NativeCatalogTrackState.Diverged || fileState == NativeAudioFileState.Diverged ||
                catalogState == NativeCatalogTrackState.Present && fileState == NativeAudioFileState.Missing ||
                !attempted && catalogState != NativeCatalogTrackState.Present)
                return new HostOutcome<ImmutableArray<PendingEffect>>.Rejected("audio-removal-baseline-changed", Conflict: true);
            if (catalogState == NativeCatalogTrackState.Present || fileState == NativeAudioFileState.Present)
                effects.Add(new(track, file, catalogState == NativeCatalogTrackState.Present));
        }
        return new HostOutcome<ImmutableArray<PendingEffect>>.Success(effects.ToImmutable());
    }

    /// <summary>Removes the native record before unlinking the verified file and independently checks both effects.</summary>
    internal static async Task<HostOutcome<bool>> ApplyAsync(ImmutableArray<PendingEffect> effects, IBeetsCatalogRemoval catalog,
        IAudioRemovalFiles audio, CancellationToken token)
    {
        foreach (var effect in effects)
        {
            // Recheck before each target in case the job was suspended after the full preflight.
            if (await audio.ObserveAudioFileAsync(effect.File, token).ConfigureAwait(false) is not
                HostOutcome<NativeAudioFileState>.Success { Value: NativeAudioFileState.Present })
                return new HostOutcome<bool>.Unavailable("audio-removal-reconciliation-incomplete");
            if (effect.CatalogPresent)
                await catalog.RemoveCatalogTrackAsync(effect.Track, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (await catalog.ObserveCatalogTrackAsync(effect.Track, token).ConfigureAwait(false) is not
                HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Missing })
                return new HostOutcome<bool>.Unavailable("audio-removal-reconciliation-incomplete");
            var deleted = await audio.DeleteAudioFileAsync(effect.File, token).ConfigureAwait(false);
            if (deleted is not HostOutcome<bool>.Success { Value: true })
                return deleted is HostOutcome<bool>.Success ? new HostOutcome<bool>.Unavailable("audio-removal-reconciliation-incomplete") : deleted;
        }
        return new HostOutcome<bool>.Success(true);
    }

    /// <summary>Checks the order and identity of approved file effects, including legacy rows without hashes.</summary>
    internal static bool ManifestMatches(ImmutableArray<MaintenanceTrack> tracks, ImmutableArray<AudioRemovalFile> files) =>
        !tracks.IsDefaultOrEmpty && !files.IsDefaultOrEmpty && files.Length == tracks.Length &&
        tracks.Zip(files).All(pair => pair.Second is not null && pair.Second.Matches(pair.First));
}
