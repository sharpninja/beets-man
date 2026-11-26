using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Prepares verified matched copies outside the library root before a live beets attempt can be recorded.</summary>
/// <remarks>2026-11-24: Apply the approved exact release without modifying originals for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed partial class BeetsGuidedImport
{
    private async Task<HostOutcome<PreparedImportAlbum?>> PrepareMatchAsync(GuidedImportWork work, MusicBrainzReleaseSearch provider,
        string ffmpeg, CancellationToken token)
    {
        var approved = work.ApprovedMatch!;
        var staged = await stage.OpenVerifiedAlbumAsync(work.Upload, token).ConfigureAwait(false);
        if (staged is not HostOutcome<VerifiedImportAlbum>.Success { Value: var input })
            return staged.PropagateFailure<PreparedImportAlbum?>();
        if (approved.Tracks.Length != input.Paths.Length || work.ProviderKey != $"musicbrainz:{approved.ReleaseId:D}")
            return ChangedMatch();

        var root = Path.Combine(workRoot, "beets-matched-" + Guid.NewGuid().ToString("N"));
        var retained = false;
        try
        {
            var copies = Directory.CreateDirectory(Path.Combine(root, "copies")).FullName;
            var settings = WriteBeetsConfig(root, Path.Combine(root, "prepare.db"), copies, albumTemplate: null, provider.Endpoint);
            await provider.WaitForRequestAsync(token).ConfigureAwait(false);
            var tagged = await RunAsync(["import", "-c", "-M", "-w", "-P", "-I", "-a", "-q", "--flat", "--search-id", approved.ReleaseId.ToString("D"),
                "--", input.Directory], false, token, settings).ConfigureAwait(false);
            if (!tagged.Succeeded)
                return UnavailableMatch("beets-match-preparation-unavailable");
            var readback = await RunAsync(["list", "-f", PreviewFormat], true, token, settings).ConfigureAwait(false);
            if (!readback.Succeeded)
                return UnavailableMatch("beets-match-preparation-unavailable");
            if (!TryMatchCopies(copies, approved, readback.Records, out var paths))
                return ChangedMatch();

            // Reading the tags again from the files distinguishes a successful write from changed database rows alone.
            var embedded = await ReadPreviewAsync(new(copies, [.. paths]), Path.Combine(root, "embedded"), null, null, token).ConfigureAwait(false);
            if (embedded is null || !embedded.Select((row, index) => MatchesApprovedTags(row, approved, index)).All(matches => matches))
                return UnavailableMatch("beets-match-tags-unverified");

            var prepared = ImmutableArray.CreateBuilder<PreparedImportTrack>(paths.Length);
            for (var index = 0; index < paths.Length; index++)
            {
                var originalAudio = await ReadAudioIdentityAsync(ffmpeg, input.Paths[index], token).ConfigureAwait(false);
                var copiedAudio = await ReadAudioIdentityAsync(ffmpeg, paths[index], token).ConfigureAwait(false);
                if (originalAudio is null || copiedAudio is null || !originalAudio.SequenceEqual(copiedAudio, StringComparer.OrdinalIgnoreCase))
                    return UnavailableMatch("beets-match-audio-unverified");
                if (!TryGetOwnedPath(copies, paths[index], out var relative))
                    return UnavailableMatch("beets-prepared-match-unavailable");
                var hashed = await ReadPreparedTrackAsync(relative, paths[index], token).ConfigureAwait(false);
                if (hashed is null)
                    return UnavailableMatch("beets-prepared-match-unavailable");
                prepared.Add(hashed);
            }
            if (await stage.OpenVerifiedAlbumAsync(work.Upload, token).ConfigureAwait(false) is not HostOutcome<VerifiedImportAlbum>.Success)
                return UnavailableMatch("import-staged-copy-unavailable");
            var album = new PreparedImportAlbum(copies, prepared.MoveToImmutable());
            if (!await VerifyPreparedAlbumAsync(album, token).ConfigureAwait(false))
                return UnavailableMatch("beets-prepared-match-unavailable");
            retained = true;
            return new HostOutcome<PreparedImportAlbum?>.Success(album);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return UnavailableMatch("beets-match-preparation-unavailable"); }
        finally
        {
            if (!retained)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool TryMatchCopies(string copies, ApprovedBeetsMatch approved, string[] rows, out string[] paths)
    {
        paths = new string[approved.Tracks.Length];
        if (rows.Length != paths.Length)
            return false;
        var indices = approved.Tracks.Select((track, index) => (track.ReleaseTrackId, index)).ToDictionary(row => row.ReleaseTrackId, row => row.index);
        foreach (var row in rows)
        {
            var fields = row.Split(ColumnSeparator);
            if (fields.Length != 12 || !TryGetOwnedPath(copies, fields[1], out _))
                return false;
            var tags = ReadTags(fields.AsSpan(2));
            if (!indices.TryGetValue(tags.ReleaseTrackId, out var index) || paths[index] is not null || !MatchesApprovedTags(tags, approved, index))
                return false;
            paths[index] = Path.GetFullPath(fields[1]);
        }
        return paths.Distinct(ImportCatalogPublication.PathComparer).Count() == paths.Length;
    }

    private static bool MatchesApprovedTags(ReadOnlyTrack? observed, ApprovedBeetsMatch approved, int index) => observed is not null &&
        observed.ReleaseId == approved.ReleaseId && observed.ReleaseTrackId == approved.Tracks[index].ReleaseTrackId &&
        observed.Title == approved.Tracks[index].Title && observed.Artist == approved.Tracks[index].Artist &&
        observed.Album == approved.Album && observed.AlbumArtist == approved.AlbumArtist && observed.Year == approved.Year &&
        observed.Month == approved.Month && observed.Day == approved.Day && observed.Country == approved.Country;

    private async Task<bool> VerifyPreparedAlbumAsync(PreparedImportAlbum album, CancellationToken token)
    {
        try
        {
            var root = Path.GetRelativePath(workRoot, album.Directory);
            var rootParts = root.Split(Path.DirectorySeparatorChar);
            if (rootParts.Length != 2 || !rootParts[0].StartsWith("beets-matched-", StringComparison.Ordinal) || rootParts[1] != "copies" ||
                !Directory.Exists(album.Directory) || HasLink(album.Directory) || HasLink(Path.GetDirectoryName(album.Directory)!))
                return false;
            var expected = album.Tracks.ToDictionary(track => track.RelativePath, ImportCatalogPublication.PathComparer);
            var directories = new Stack<string>();
            directories.Push(album.Directory);
            while (directories.TryPop(out var directory))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return false;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(path);
                        continue;
                    }
                    if (!TryGetOwnedPath(album.Directory, path, out var relative) || !expected.Remove(relative, out var clean) ||
                        await ReadPreparedTrackAsync(relative, path, token).ConfigureAwait(false) != clean)
                        return false;
                }
            }
            return expected.Count == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool HasLink(string directory) => (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;

    private static async Task<PreparedImportTrack?> ReadPreparedTrackAsync(string relative, string path, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var size = source.Length;
        var hash = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
        return source.Position == size && source.Length == size ? new(relative, size, Convert.ToHexString(hash)) : null;
    }

    private static async Task<string[]?> ReadAudioIdentityAsync(string executable, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-v", "error", "-nostdin", "-i", path, "-map", "0:a", "-c", "copy",
                     "-f", "streamhash", "-hash", "sha256", "-" })
            start.ArgumentList.Add(argument);
        var output = await ExecuteProcessAsync(start, "", token).ConfigureAwait(false);
        return output.Succeeded && output.Records.Length > 0 && output.Records.All(IsAudioHash) ? output.Records : null;
    }

    private static bool IsAudioHash(string row)
    {
        var parts = row.Split(',');
        return parts.Length == 3 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            parts[1] == "a" && parts[2].StartsWith("SHA256=", StringComparison.Ordinal) && parts[2].Length == 71 && parts[2][7..].All(Uri.IsHexDigit);
    }

    private static HostOutcome<PreparedImportAlbum?> ChangedMatch() => new HostOutcome<PreparedImportAlbum?>.Rejected("approved-import-match-changed");

    private static HostOutcome<PreparedImportAlbum?> UnavailableMatch(string code) => new HostOutcome<PreparedImportAlbum?>.Unavailable(code);
}
