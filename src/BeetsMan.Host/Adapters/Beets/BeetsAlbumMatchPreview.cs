using System.Collections.Immutable;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Previews exact remote releases in disposable beets catalogs without modifying staged media.</summary>
/// <remarks>2026-11-24: Prepare the ordered SCR37 tag comparison for FR-BEETS-001#ac-import.</remarks>
internal sealed partial class BeetsGuidedImport
{
    private static readonly string TagPreviewFields = string.Join(ColumnSeparator,
        "${title}", "${artist}", "${album}", "${albumartist}", "${mb_albumid}", "${mb_releasetrackid}", "${year}", "${month}", "${day}", "${country}");
    private static readonly string PreviewFormat = string.Join(ColumnSeparator, RecordPrefix, "${path}", TagPreviewFields);

    /// <summary>Reads the original album tags, then compares candidate releases using the installed beets matcher.</summary>
    internal async Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> PreviewMatchesAsync(ImportUpload upload,
        MusicBrainzReleaseSearch provider, CancellationToken token)
    {
        var staged = await stage.OpenVerifiedAlbumAsync(upload, token).ConfigureAwait(false);
        if (staged is not HostOutcome<VerifiedImportAlbum>.Success { Value: var album })
            return staged.PropagateFailure<ImmutableArray<BeetsImportCandidate>>();

        var folder = Path.Combine(workRoot, "beets-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var sources = await ReadPreviewAsync(album, Path.Combine(folder, "original"), null, null, token).ConfigureAwait(false);
            if (sources is null || sources.Any(row => row is null))
                return new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Unavailable("beets-source-tags-unavailable");
            var original = sources.OfType<ReadOnlyTrack>().ToArray();
            var albums = original.Select(row => row.Album).Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (albums.Length != 1)
                return new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success([]);
            var artists = original.Select(row => string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.Artist : row.AlbumArtist)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = await provider.SearchAsync(albums[0], artists.Length == 1 ? artists[0] : null, token).ConfigureAwait(false);
            if (candidates is not HostOutcome<ImmutableArray<MusicBrainzRelease>>.Success { Value: var releases })
                return candidates.PropagateFailure<ImmutableArray<BeetsImportCandidate>>();

            var previews = ImmutableArray.CreateBuilder<BeetsImportCandidate>(releases.Length);
            foreach (var release in releases)
            {
                await provider.WaitForRequestAsync(token).ConfigureAwait(false);
                var matched = await ReadPreviewAsync(album, Path.Combine(folder, release.Id.ToString("N")), release.Id, provider.Endpoint, token).ConfigureAwait(false);
                var comparison = ImmutableArray.CreateBuilder<ImportTrackComparison>(original.Length);
                var complete = true;
                for (var index = 0; index < original.Length; index++)
                {
                    var suggestion = matched?[index];
                    var title = suggestion is { ReleaseId: var id, ReleaseTrackId: var trackId } && id == release.Id &&
                        trackId != Guid.Empty && !string.IsNullOrWhiteSpace(suggestion.Title) ? suggestion.Title : null;
                    complete &= title is not null;
                    var sourceTitle = string.IsNullOrWhiteSpace(original[index].Title)
                        ? Path.GetFileNameWithoutExtension(upload.Files[index].Name) : original[index].Title;
                    comparison.Add(new(index, sourceTitle, title));
                }
                var plan = complete && (release.TrackCount is null || release.TrackCount == original.Length)
                    ? PinNativeMatch(release.Id, matched!) : null;
                previews.Add(new(release.ProviderKey, release.Title, release.Artist, new(release.Date, release.Country, release.TrackCount,
                    plan is not null, comparison.MoveToImmutable()), plan));
            }
            return new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success(previews.MoveToImmutable());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Unavailable("beets-match-preview-unavailable"); }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<ReadOnlyTrack?[]?> ReadPreviewAsync(VerifiedImportAlbum album, string root, Guid? release, Uri? provider, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        var config = WriteBeetsConfig(root, Path.Combine(root, "preview.db"), root, albumTemplate: null, provider);
        string[] arguments = release is { } exact
            ? ["import", "-C", "-M", "-W", "-P", "-I", "-a", "-q", "--flat", "--search-id", exact.ToString("D"), "--", album.Directory]
            : ["import", "-C", "-M", "-W", "-P", "-I", "-A", "-q", "--flat", "--", album.Directory];
        var attempted = await RunAsync(arguments, false, token, config).ConfigureAwait(false);
        if (!attempted.Succeeded)
            return null;
        var native = await RunAsync(["list", "-f", PreviewFormat], true, token, config).ConfigureAwait(false);
        if (!native.Succeeded || native.Records.Length > album.Paths.Length)
            return null;

        var indices = album.Paths.Select((path, index) => (path, index)).ToDictionary(file => file.path, file => file.index, ImportCatalogPublication.PathComparer);
        var matched = new ReadOnlyTrack?[album.Paths.Length];
        foreach (var line in native.Records)
        {
            var columns = line.Split(ColumnSeparator);
            if (columns.Length != 12 || !Path.IsPathFullyQualified(columns[1]) ||
                !indices.TryGetValue(Path.GetFullPath(columns[1]), out var index) || matched[index] is not null)
                return null;
            matched[index] = ReadTags(columns.AsSpan(2));
        }
        return matched;
    }

    private static ReadOnlyTrack ReadTags(ReadOnlySpan<string> fields)
    {
        Guid.TryParseExact(fields[4], "D", out var id);
        Guid.TryParseExact(fields[5], "D", out var trackId);
        return new(fields[0], fields[1], fields[2], fields[3], id, trackId, fields[6], fields[7], fields[8], fields[9]);
    }

    private static ApprovedBeetsMatch? PinNativeMatch(Guid releaseId, ReadOnlyTrack?[] rows)
    {
        var tracks = rows.OfType<ReadOnlyTrack>().ToArray();
        if (tracks.Length != rows.Length || tracks.Length == 0 || tracks.Any(track => track.ReleaseId != releaseId ||
                track.ReleaseTrackId == Guid.Empty || string.IsNullOrWhiteSpace(track.Title)) ||
            tracks.Select(track => track.ReleaseTrackId).Distinct().Count() != tracks.Length)
            return null;
        var head = tracks[0];
        if (tracks.Any(track => track.Album != head.Album || track.AlbumArtist != head.AlbumArtist || track.Year != head.Year ||
                track.Month != head.Month || track.Day != head.Day || track.Country != head.Country))
            return null;
        return new(releaseId, head.Album, head.AlbumArtist, head.Year, head.Month, head.Day, head.Country,
            [.. tracks.Select(track => new ApprovedBeetsTrack(track.ReleaseTrackId, track.Title, track.Artist))]);
    }

    private sealed record ReadOnlyTrack(string Title, string Artist, string Album, string AlbumArtist, Guid ReleaseId, Guid ReleaseTrackId,
        string Year, string Month, string Day, string Country);
}
