using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Resolves cached media paths inside the configured local music directory.</summary>
/// <remarks>2026-11-24: Resolve downloaded and pending media for FR-LIBRARY-001#ac-offline and TR-LIBRARY-ARCH-001.</remarks>
internal sealed class LocalCatalogMedia(string root)
{
    private readonly string rootPath = Path.GetFullPath(root);
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Returns a playable path only when the current local file is under the resolved root.</summary>
    public (TrackAvailability Availability, string? FullPath) Resolve(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return (TrackAvailability.Unavailable, null);

        var parts = relativePath.Split(['/', '\\']);
        if (parts.Any(part => part is "" or "." or ".." || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return (TrackAvailability.Unavailable, null);

        try
        {
            var actualRoot = DesktopBootstrapEnvironment.ResolvePhysicalPath(rootPath);
            var prefix = Path.EndsInDirectorySeparator(actualRoot) ? actualRoot : actualRoot + Path.DirectorySeparatorChar;
            var mapped = DesktopBootstrapEnvironment.ResolvePhysicalPath(Path.Combine(rootPath, Path.Combine(parts)));
            if (!mapped.StartsWith(prefix, Comparison))
                return (TrackAvailability.Unavailable, null);
            return File.Exists(mapped) ? (TrackAvailability.Downloaded, mapped) : (TrackAvailability.Pending, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (TrackAvailability.Unavailable, null);
        }
    }
}
