using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.HostLibrary;

/// <summary>Uses the cached baseline and ordered stage entries to present unpublished tag edits.</summary>
/// <remarks>2026-11-24: Overlay and restore local tag edits for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed partial class ImportIntakeHandlers
{
    private async Task<Outcome<TrackTagSnapshot>> ReadWithBatchAsync(LibraryBatchHandlers batch, GetTrackTags request, RequestContext context,
        CancellationToken token)
    {
        var staged = await batch.ReadTrackChangesAsync(request.TrackId, context, token).ConfigureAwait(false);
        if (staged is not Outcome<LibraryBatchHandlers.TrackBatchOverlay?>.Success { Value: var changes })
            return LibraryBatchHandlers.CopyFailure<LibraryBatchHandlers.TrackBatchOverlay?, TrackTagSnapshot>(staged);
        return changes is { } pending ? await ProjectLocalTagsAsync(request.TrackId, pending, token).ConfigureAwait(false) :
            await host.ReadTagsAsync(request, context, token).ConfigureAwait(false);
    }

    private async Task<Outcome<LibraryBatchChange.TrackTags>> PrepareTagEditAsync(LibraryBatchHandlers batch, EditTrackTags request,
        RequestContext context, CancellationToken token)
    {
        var changes = Normalize(request.TagChanges);
        if (request.TrackId == Guid.Empty || request.ExpectedCatalogRevision <= 0 || changes is null)
            return new Outcome<LibraryBatchChange.TrackTags>.Invalid([new("invalid-track-tag-edit")]);

        var baseline = await ReadWithBatchAsync(batch, new(request.TrackId), context, token).ConfigureAwait(false);
        if (baseline is not Outcome<TrackTagSnapshot>.Success { Value: var snapshot })
            return LibraryBatchHandlers.CopyFailure<TrackTagSnapshot, LibraryBatchChange.TrackTags>(baseline);
        if (snapshot.CatalogRevision != request.ExpectedCatalogRevision)
            return new Outcome<LibraryBatchChange.TrackTags>.Conflict("stale-track-tags", snapshot.CatalogRevision);
        return new Outcome<LibraryBatchChange.TrackTags>.Success(new(request.TrackId, snapshot.CatalogRevision,
            snapshot.Tags.ToImmutableSortedDictionary(StringComparer.Ordinal), changes));
    }

    private Task<Outcome<TrackTagSnapshot>> ProjectLocalTagsAsync(Guid trackId, LibraryBatchHandlers.TrackBatchOverlay pending, CancellationToken token)
    {
        if (pending.Changes.IsEmpty)
            return cachedTags?.ReadCachedTrackTagsAsync(trackId, token) ??
                Task.FromResult<Outcome<TrackTagSnapshot>>(new Outcome<TrackTagSnapshot>.DependencyFailure("cached-track-tags-unavailable", true));
        var first = pending.Changes[0];
        var tags = first.Before.ToImmutableDictionary(StringComparer.Ordinal).ToBuilder();
        foreach (var edit in pending.Changes)
        foreach (var (field, value) in edit.Changes)
        {
            if (string.IsNullOrEmpty(value))
                tags.Remove(field);
            else
                tags[field] = value;
        }
        return Task.FromResult<Outcome<TrackTagSnapshot>>(new Outcome<TrackTagSnapshot>.Success(new(trackId, first.ExpectedRevision, tags.ToImmutable(), pending.BatchId)));
    }

    private static bool SameTags(ImmutableSortedDictionary<string, string?> stored, ImmutableDictionary<string, string?> request) =>
        Normalize(request) is { } normalized && stored.SequenceEqual(normalized);

    private static ImmutableSortedDictionary<string, string?>? Normalize(ImmutableDictionary<string, string?> request)
    {
        if (request is not { Count: > 0 })
            return null;
        var canonical = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (var (field, source) in request)
        {
            var key = field.ToLowerInvariant();
            if (key is not ("title" or "artist" or "albumartist" or "album" or "year" or "disc" or "track" or "genre") ||
                canonical.ContainsKey(key) || source is { Length: > 4096 } || (source is not null && source.Any(char.IsControl)))
                return null;
            var value = source;
            if (source is not null && key is ("year" or "disc" or "track"))
            {
                if (!int.TryParse(source, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                    (key == "year" && number > 9999))
                    return null;
                value = number.ToString(CultureInfo.InvariantCulture);
            }
            canonical.Add(key, value);
        }
        return canonical.ToImmutable();
    }
}
