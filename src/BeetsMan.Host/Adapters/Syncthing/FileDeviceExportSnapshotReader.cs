using System.Security.Cryptography;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Reads relevant paths from one host-local Android export share.</summary>
/// <remarks>2026-11-24: Use observed path types and file content for FR-FILESYNC-001#ac-safety before publication.</remarks>
public sealed class FileDeviceExportSnapshotReader : IDeviceExportShareReader
{
    private readonly string root;
    private readonly RegisteredAndroidExport owner;

    /// <summary>Binds the reader to the host-configured root for a registered export.</summary>
    public FileDeviceExportSnapshotReader(string root, RegisteredAndroidExport owner)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("The export root must be absolute.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(this.root) == this.root)
            throw new ArgumentException("The export root must be a dedicated directory.", nameof(root));
        this.owner = owner;
    }

    /// <summary>Observes exact paths and their ancestors without traversing links or changing files.</summary>
    public async Task<HostOutcome<DeviceExportShareSnapshot>> ReadAsync(RegisteredAndroidExport target, IEnumerable<string> paths, CancellationToken token)
    {
        if (target.LibraryId != owner.LibraryId || target.DeviceId != owner.DeviceId || target.ShareId != owner.ShareId ||
            target.Revision != owner.Revision || target.CaseSensitivePaths != owner.CaseSensitivePaths)
            return new HostOutcome<DeviceExportShareSnapshot>.Rejected("export-share-scope-mismatch");

        var comparison = target.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var selected = paths.Distinct(comparison).ToArray();
        if (selected.Any(path => !PlaylistExportPlanner.TryNormalize(path, out var normalized) || path != normalized))
            return new HostOutcome<DeviceExportShareSnapshot>.Rejected("invalid-export-publication-path");

        try
        {
            token.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(root);
            if (!IsOrdinaryDirectory(directory))
                return new HostOutcome<DeviceExportShareSnapshot>.Unavailable("export-share-unavailable");
            var cache = new Dictionary<string, ILookup<string, FileSystemInfo>>(StringComparer.Ordinal);
            var observed = new Dictionary<string, ObservedDeviceExportPath>(StringComparer.Ordinal);
            foreach (var path in selected)
            {
                DirectoryInfo current = directory;
                var parts = path.Split('/');
                string? relative = null;
                for (var index = 0; index < parts.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!cache.TryGetValue(current.FullName, out var items))
                    {
                        items = current.EnumerateFileSystemInfos().ToLookup(item => item.Name, comparison);
                        cache.Add(current.FullName, items);
                    }

                    var matches = items[parts[index]].Take(2).ToArray();
                    if (matches.Length == 0)
                    {
                        if (target.CaseSensitivePaths && Path.Exists(Path.Combine(current.FullName, parts[index])))
                            return new HostOutcome<DeviceExportShareSnapshot>.Rejected("export-share-case-collision");
                        break;
                    }
                    if (matches.Length > 1)
                        return new HostOutcome<DeviceExportShareSnapshot>.Rejected("export-share-case-collision");
                    var entry = matches[0];
                    relative = relative is null ? entry.Name : $"{relative}/{entry.Name}";
                    if (!observed.TryGetValue(relative, out var detail) ||
                        index == parts.Length - 1 && detail.Kind == DeviceExportPathKind.File && detail.ContentIdentity is null)
                    {
                        detail = await DescribeAsync(relative, entry, index == parts.Length - 1, token).ConfigureAwait(false);
                        observed[relative] = detail;
                    }
                    if (detail.Kind != DeviceExportPathKind.Directory)
                        break;
                    current = new(entry.FullName);
                }
            }
            return new HostOutcome<DeviceExportShareSnapshot>.Success(new(target.LibraryId, target.DeviceId, target.ShareId,
                [.. observed.Values.OrderBy(item => item.Path, StringComparer.Ordinal)]));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<DeviceExportShareSnapshot>.Cancelled(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<DeviceExportShareSnapshot>.Unavailable("export-share-unavailable");
        }
    }

    /// <summary>Checks a configured directory and its ancestors before reading or modifying an export.</summary>
    internal static bool IsOrdinaryDirectory(DirectoryInfo root)
    {
        for (DirectoryInfo? current = root; current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static async Task<ObservedDeviceExportPath> DescribeAsync(string path, FileSystemInfo entry, bool readContent, CancellationToken token)
    {
        entry.Refresh();
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return new(path, DeviceExportPathKind.Link);
        if ((attributes & FileAttributes.Directory) != 0)
            return new(path, DeviceExportPathKind.Directory);
        if (readContent && entry is FileInfo file)
        {
            var written = file.LastWriteTimeUtc;
            await using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != written || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An export path changed while its content was being observed.");
            return new(path, DeviceExportPathKind.File, hash, length);
        }
        return new(path, DeviceExportPathKind.File);
    }
}
