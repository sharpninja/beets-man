namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Opens regular catalog audio from the configured host library root.</summary>
/// <remarks>2026-11-24: Apply the same path rules at drop and preview for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
internal sealed class HostExportMedia
{
    private readonly string root;

    public HostExportMedia(string musicRoot)
    {
        if (!Path.IsPathFullyQualified(musicRoot))
            throw new ArgumentException("The host media root must be an absolute path.", nameof(musicRoot));
        root = Path.GetFullPath(musicRoot);
    }

    public Stream? Open(string relativePath)
    {
        if (!TryNormalize(relativePath, out var normalized))
            return null;
        var path = root;
        try
        {
            foreach (var segment in normalized.Split('/'))
            {
                path = Path.Combine(path, segment);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            if (new FileInfo(path).Length == 0)
                return null;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool TryNormalize(string relativePath, out string normalized)
    {
        normalized = relativePath.Replace('\\', '/');
        var invalid = Path.GetInvalidFileNameChars();
        return normalized.Split('/').All(segment => segment is not ("" or "." or "..") && segment.IndexOfAny(invalid) < 0);
    }
}
