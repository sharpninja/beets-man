namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Compares configured receiver paths with the receiving system's native path rules.</summary>
/// <remarks>2026-11-24: Validate FR-FILESYNC-001#ac-mirror without treating Windows paths as local Unix paths.</remarks>
internal static class PcMirrorNativePath
{
    /// <summary>Checks that a receiver root is absolute, dedicated and has unambiguous segments.</summary>
    internal static bool IsDedicatedRoot(string path) => Normalize(path) is not null;

    /// <summary>Checks an observed Syncthing path against its configured absolute receiver root.</summary>
    internal static bool Matches(string observed, string expected) => Normalize(observed) is { } left && Normalize(expected) is { } right &&
        left.Windows == right.Windows && string.Equals(left.Path, right.Path,
            left.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static (string Path, bool Windows)? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return null;
        var drive = path is [var driveLetter, ':', '/' or '\\', ..] && char.IsAsciiLetter(driveLetter);
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        var windows = drive || unc;
        var canonical = (windows ? path.Replace('\\', '/') : path).TrimEnd('/');
        var prefix = unc ? 2 : drive ? 3 : 1;
        if (!windows && (!path.StartsWith('/') || path.Contains('\\')) || canonical.Length <= prefix)
            return null;
        var segments = canonical[prefix..].Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || windows &&
            (segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)) ||
            unc && segments.Length < 3)
            return null;
        return (canonical, windows);
    }
}
