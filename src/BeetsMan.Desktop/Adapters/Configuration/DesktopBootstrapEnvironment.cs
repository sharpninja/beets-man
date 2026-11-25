using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Checks local storage paths and verifies the desktop LiteDB schema during bootstrap.</summary>
/// <remarks>2026-11-24: Bind the local filesystem and validated store to FR-CONFIG-001#ac-repair.</remarks>
public sealed class DesktopBootstrapEnvironment(IEnumerable<string> sharedRoots) : IBootstrapEnvironment
{
    private readonly string[] roots = sharedRoots.Select(ResolvePhysicalPath).ToArray();
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public bool IsShared(string absolutePath)
    {
        var actual = ResolvePhysicalPath(absolutePath);
        if (OperatingSystem.IsWindows() && actual.StartsWith("\\\\", StringComparison.Ordinal))
            return true;
        return roots.Any(root => string.Equals(root, actual, Comparison) || actual.StartsWith(Path.EndsInDirectorySeparator(root)
            ? root : root + Path.DirectorySeparatorChar, Comparison));
    }

    /// <inheritdoc />
    public bool DatabaseExists(string absolutePath) => File.Exists(absolutePath);

    /// <inheritdoc />
    public bool MusicAvailable(string absolutePath) => Directory.Exists(absolutePath);

    /// <inheritdoc />
    public Task<ValidationIssue?> ValidateDatabaseAsync(string absolutePath, bool allowCreate, CancellationToken cancellationToken) =>
        Task.Run<ValidationIssue?>(() => Verify(absolutePath, allowCreate), cancellationToken);

    private ValidationIssue? Verify(string path, bool allowCreate)
    {
        if (IsShared(path))
            return new("invalid-database-path", nameof(BootstrapSettings.DatabasePath));
        try
        {
            using var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate);
            return null;
        }
        catch (Exception exception) when (exception is LiteException or IOException or UnauthorizedAccessException)
        {
            return new("desktop-database-unavailable", nameof(BootstrapSettings.DatabasePath));
        }
    }

    // 2026-11-24: Reuse resolved filesystem paths for FR-LIBRARY-001#ac-offline.
    internal static string ResolvePhysicalPath(string absolute)
    {
        var path = Path.GetFullPath(absolute);
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var segment in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var point = Path.Combine(resolved, segment);
            FileSystemInfo node = Directory.Exists(point) ? new DirectoryInfo(point) : new FileInfo(point);
            resolved = node.LinkTarget is null ? point : node.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? point;
        }
        return Path.TrimEndingDirectorySeparator(resolved);
    }
}
