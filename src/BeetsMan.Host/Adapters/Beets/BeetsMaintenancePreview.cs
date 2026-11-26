using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Reads exact beets records and previews file organization without changing the catalog or files.</summary>
/// <remarks>2026-11-24: Resolve native STB18 preview paths for FR-BEETS-001#ac-edit.</remarks>
internal sealed partial class BeetsGuidedImport : IBeetsMaintenancePlanner
{
    private static readonly string MaintenanceIdentityFormat = string.Join(ColumnSeparator, RecordPrefix, "${id}", "${path}");

    /// <inheritdoc />
    async Task<HostOutcome<ImmutableArray<MaintenancePathChange>>> IBeetsMaintenancePlanner.PreviewMovesAsync(
        ImmutableArray<MaintenanceTrack> tracks, CancellationToken token)
    {
        var moves = ImmutableArray.CreateBuilder<MaintenancePathChange>();
        try
        {
            foreach (var track in tracks)
            {
                var source = await ReadMaintenanceSourceAsync(track, token).ConfigureAwait(false);
                if (source is null)
                    return Unverified<ImmutableArray<MaintenancePathChange>>();
                var result = await RunAsync(["move", "--pretend", "--", TrackQuery(track.BeetsTrackId)], false, token, captureDisplay: true)
                    .ConfigureAwait(false);
                if (!result.Succeeded || !TryReadDestination(result.Records, source, out var proposed))
                    return Unverified<ImmutableArray<MaintenancePathChange>>();
                if (proposed is null)
                    continue;
                if (!TryGetFreeDestination(proposed, out var destination))
                    return new HostOutcome<ImmutableArray<MaintenancePathChange>>.Rejected("maintenance-destination-unavailable", Conflict: true);
                moves.Add(new(track.TrackId, track.RelativePath, destination));
            }
            return new HostOutcome<ImmutableArray<MaintenancePathChange>>.Success(moves.ToImmutable());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Unverified<ImmutableArray<MaintenancePathChange>>(); }
    }

    /// <inheritdoc />
    async Task<HostOutcome<ImmutableArray<AudioRemovalFile>>> IBeetsMaintenancePlanner.PreviewAudioRemovalAsync(ImmutableArray<MaintenanceTrack> tracks,
        CancellationToken token)
    {
        try
        {
            var files = ImmutableArray.CreateBuilder<AudioRemovalFile>(tracks.Length);
            foreach (var track in tracks)
            {
                if (await ReadMaintenanceSourceAsync(track, token).ConfigureAwait(false) is not { } path ||
                    await ReadMediaFingerprintAsync(path, token).ConfigureAwait(false) is not { } fingerprint)
                    return Unverified<ImmutableArray<AudioRemovalFile>>();
                files.Add(new(track.RelativePath, fingerprint.Size, fingerprint.Sha256));
            }
            return new HostOutcome<ImmutableArray<AudioRemovalFile>>.Success(files.MoveToImmutable());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Unverified<ImmutableArray<AudioRemovalFile>>(); }
    }

    private async Task<string?> ReadMaintenanceSourceAsync(MaintenanceTrack target, CancellationToken token)
    {
        var result = await ReadCatalogPathAsync(target.BeetsTrackId, token).ConfigureAwait(false);
        return result is HostOutcome<string?>.Success { Value: { } path } && TryGetOwnedPath(musicRoot, path, out var relative) &&
            SamePath(target.RelativePath, relative) ? path : null;
    }

    private static bool TryReadDestination(string[] lines, string source, out string? destination)
    {
        destination = null;
        if (lines.Length == 0)
            return true;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.StartsWith(source, StringComparison.Ordinal))
                continue;
            var suffix = line[source.Length..].TrimStart();
            if (suffix.Length == 0 && index + 1 < lines.Length)
                suffix = lines[++index].TrimStart();
            if (!suffix.StartsWith("-> ", StringComparison.Ordinal) || destination is not null)
                return false;
            destination = suffix[3..];
        }
        return destination is { Length: > 0 };
    }

    private bool TryGetFreeDestination(string proposed, out string relative)
    {
        relative = "";
        if (!Path.IsPathFullyQualified(proposed))
            return false;
        var target = Path.GetFullPath(proposed);
        var path = Path.GetRelativePath(musicRoot, target).Replace(Path.DirectorySeparatorChar, '/');
        if (LibraryMediaPath(path) is null || File.Exists(target))
            return false;
        relative = path;
        return true;
    }

    private static HostOutcome<T> Unverified<T>() => new HostOutcome<T>.Unavailable("beets-maintenance-preview-unverified");
}
