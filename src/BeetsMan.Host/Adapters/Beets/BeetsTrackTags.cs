using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Writes one selected beets item and verifies database and embedded tags independently.</summary>
/// <remarks>2026-11-24: Verify existing-track tag edits and recover interrupted writes for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed partial class BeetsGuidedImport : IBeetsTrackTags
{
    private static readonly string[] EditableFields = ["title", "artist", "albumartist", "album", "year", "disc", "track", "genre"];
    private static readonly string TagReadbackFormat = string.Join(ColumnSeparator, RecordPrefix, "${id}", "${path}",
        "${title}", "${artist}", "${albumartist}", "${album}", "${year}", "${disc}", "${track}", "${genres}");

    /// <inheritdoc />
    async Task<HostOutcome<NativeTrackTags>> IBeetsTrackTags.ObserveAsync(TrackTagWork work, CancellationToken token)
    {
        try
        {
            if (await ReadSelectedTrackAsync(work, token).ConfigureAwait(false) is not { } selected)
                return new HostOutcome<NativeTrackTags>.Unavailable("track-tag-native-item-unavailable");
            var embedded = await ObserveEmbeddedTagsAsync(selected.Path, token).ConfigureAwait(false);
            return embedded is null ? new HostOutcome<NativeTrackTags>.Unavailable("track-tag-embedded-readback-unavailable") :
                new HostOutcome<NativeTrackTags>.Success(new(selected.Tags, embedded));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HostOutcome<NativeTrackTags>.Unavailable("track-tag-readback-unavailable");
        }
    }

    /// <inheritdoc />
    async Task<HostOutcome<bool>> IBeetsTrackTags.ApplyAsync(TrackTagWork work, CancellationToken token)
    {
        string? folder = null;
        string? settings = null;
        try
        {
            if (!work.NativeAttempted || await ReadSelectedTrackAsync(work, token).ConfigureAwait(false) is null)
                return new HostOutcome<bool>.Success(false);
            var cleared = work.Changes.Where(change => string.IsNullOrEmpty(change.Value)).Select(change => NativeField(change.Key)).ToArray();
            if (cleared.Length > 0)
            {
                var nativeConfig = await PrepareConfigAsync(token).ConfigureAwait(false);
                folder = Path.Combine(Path.GetDirectoryName(nativeConfig)!, "tag-clear-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                settings = WriteBeetsConfig(folder, options.CatalogPath, musicRoot, options.AlbumPathTemplate, clearedFields: cleared);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DeleteTagScratch(folder);
            return new HostOutcome<bool>.Success(false);
        }

        var assignments = work.Changes.OrderBy(change => change.Key, StringComparer.Ordinal).Select(change =>
        {
            var field = NativeField(change.Key);
            var value = change.Value ?? (IsTagNumber(field) ? "0" : "");
            // Dollar is the escape character in beets's value templates.
            var literal = value.Replace("$", "$$", StringComparison.Ordinal).Replace("%", "$%", StringComparison.Ordinal);
            return $"{field}={literal}";
        });
        try
        {
            var result = await RunAsync(["modify", "-M", "-w", "-y", "--", TrackQuery(work.BeetsTrackId), .. assignments], false, token, settings)
                .ConfigureAwait(false);
            return new HostOutcome<bool>.Success(result.Started);
        }
        finally { DeleteTagScratch(folder); }
    }

    private async Task<(string Path, ImmutableDictionary<string, string> Tags)?> ReadSelectedTrackAsync(TrackTagWork work, CancellationToken token)
    {
        var result = await RunAsync(["list", "-f", TagReadbackFormat, TrackQuery(work.BeetsTrackId)], true, token).ConfigureAwait(false);
        return TryReadTagRow(result, out var id, out var path, out var tags) && id == work.BeetsTrackId &&
            TryGetOwnedPath(musicRoot, path, out var relative) && ImportCatalogPublication.PathComparer.Equals(work.RelativePath, relative)
            ? (path, tags) : null;
    }

    private async Task<ImmutableDictionary<string, string>?> ObserveEmbeddedTagsAsync(string path, CancellationToken token)
    {
        var nativeConfig = await PrepareConfigAsync(token).ConfigureAwait(false);
        var folder = Path.Combine(Path.GetDirectoryName(nativeConfig)!, "tag-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings = WriteBeetsConfig(folder, Path.Combine(folder, "readback.db"), folder, null);
            var import = await RunAsync(["import", "-C", "-M", "-W", "-P", "-I", "-A", "-s", "-q", "--", path], false, token, settings)
                .ConfigureAwait(false);
            if (!import.Succeeded)
                return null;
            var readback = await RunAsync(["list", "-f", TagReadbackFormat], true, token, settings).ConfigureAwait(false);
            return TryReadTagRow(readback, out _, out var observedPath, out var tags) && ImportCatalogPublication.PathComparer.Equals(path, observedPath)
                ? tags : null;
        }
        finally { DeleteTagScratch(folder); }
    }

    private static bool TryReadTagRow(ProcessReadback result, out long id, out string path, out ImmutableDictionary<string, string> tags)
    {
        id = 0;
        path = "";
        tags = ImmutableDictionary<string, string>.Empty;
        if (!result.Succeeded || result.Records.Length != 1)
            return false;
        var columns = result.Records[0].Split(ColumnSeparator);
        if (columns.Length != EditableFields.Length + 3 || !long.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
            return false;
        path = columns[2];
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < EditableFields.Length; index++)
        {
            var field = EditableFields[index];
            var value = columns[index + 3];
            if (IsTagNumber(field))
                value = PositiveNumber(value)?.ToString(CultureInfo.InvariantCulture) ?? "";
            if (value.Length != 0)
                values.Add(field, value);
        }
        tags = values.ToImmutable();
        return true;
    }

    private static bool IsTagNumber(string field) => field is "year" or "disc" or "track";

    private static string NativeField(string field) => field == "genre" ? "genres" : field;

    private static void DeleteTagScratch(string? folder)
    {
        if (folder is null)
            return;
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string TrackQuery(long trackId) => string.Create(CultureInfo.InvariantCulture, $"id:{trackId}");
}
