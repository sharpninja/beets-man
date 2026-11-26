using System.Globalization;
using System.Text.RegularExpressions;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Removes pinned items from the native catalog without requesting audio deletion.</summary>
/// <remarks>2026-11-24: Reconcile exact native IDs and paths for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed partial class BeetsGuidedImport : IBeetsCatalogRemoval
{
    /// <inheritdoc />
    async Task<HostOutcome<NativeCatalogTrackState>> IBeetsCatalogRemoval.ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
    {
        var observed = await ReadCatalogPathAsync(track.BeetsTrackId, token).ConfigureAwait(false);
        if (observed is HostOutcome<string?>.Rejected)
            return new HostOutcome<NativeCatalogTrackState>.Success(NativeCatalogTrackState.Diverged);
        if (observed is not HostOutcome<string?>.Success { Value: var absolute })
            return observed.PropagateFailure<NativeCatalogTrackState>();
        if (absolute is null)
            return new HostOutcome<NativeCatalogTrackState>.Success(NativeCatalogTrackState.Missing);
        var relative = Path.GetRelativePath(musicRoot, absolute).Replace(Path.DirectorySeparatorChar, '/');
        return new HostOutcome<NativeCatalogTrackState>.Success(ImportCatalogPublication.PathComparer.Equals(relative, track.RelativePath)
            ? NativeCatalogTrackState.Present : NativeCatalogTrackState.Diverged);
    }

    /// <summary>Reads exactly one native identity and its absolute path for FR-BEETS-001#ac-recover.</summary>
    private async Task<HostOutcome<string?>> ReadCatalogPathAsync(long beetsId, CancellationToken token)
    {
        var result = await RunAsync(["list", "-f", MaintenanceIdentityFormat, "--", TrackQuery(beetsId)], true, token).ConfigureAwait(false);
        if (!result.Succeeded || result.Records.Length > 1)
            return new HostOutcome<string?>.Unavailable("maintenance-native-catalog-unavailable");
        if (result.Records.Length == 0)
            return new HostOutcome<string?>.Success(null);

        var fields = result.Records[0].Split(ColumnSeparator);
        if (fields.Length != 3 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var nativeId) ||
            nativeId != beetsId || !Path.IsPathFullyQualified(fields[2]))
            return new HostOutcome<string?>.Rejected("maintenance-native-track-changed", Conflict: true);
        return new HostOutcome<string?>.Success(Path.GetFullPath(fields[2]));
    }

    /// <inheritdoc />
    async Task<HostOutcome<bool>> IBeetsCatalogRemoval.RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
    {
        if (track.BeetsTrackId <= 0 || !HostLibraryMaintenance.IsLibraryRelativePath(track.RelativePath))
            return new HostOutcome<bool>.Rejected("maintenance-native-track-changed", Conflict: true);
        var result = await RunAsync(["remove", "--force", "--", TrackQuery(track.BeetsTrackId), ExactMaintenancePathQuery(track.RelativePath)], false, token).ConfigureAwait(false);
        return new HostOutcome<bool>.Success(result.Succeeded);
    }

    /// <summary>Matches only the approved source path on native beets storage formats for FR-BEETS-001#ac-edit.</summary>
    private string ExactMaintenancePathQuery(string relativePath)
    {
        var absolute = Path.Combine(musicRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var paths = new[] { relativePath, absolute, absolute.Replace('\\', '/') }.Distinct(StringComparer.Ordinal).Select(Regex.Escape);
        var prefix = OperatingSystem.IsWindows() ? "(?i:" : "(?:";
        return $"path::\\A{prefix}{string.Join('|', paths)})\\Z";
    }
}
