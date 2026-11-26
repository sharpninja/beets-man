using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Reads cached catalog projections at the committed library feed revision.</summary>
/// <remarks>2026-11-24: Implement FR-LIBRARY-001#ac-browse and ac-offline using the desktop entity collections.</remarks>
/// <remarks>2026-11-24: Read the local tag baseline for FR-LIBRARY-002#ac-batch-local.</remarks>
/// <remarks>2026-11-24: Project staged maintenance in the owning desktop's working catalog for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed class LiteDbCatalogReader : IQueryHandler<BrowseCatalog, CatalogPage>,
    IQueryHandler<GetTrackAvailability, TrackAvailabilitySnapshot>, IPublishedPlaylistReader, ICachedTrackTags
{
    private readonly LiteDbDesktopPlaylistStore store;
    private readonly Guid libraryId;
    private readonly Func<bool> isOffline;
    private readonly LocalCatalogMedia media;
    private readonly int pageSize;
    private readonly LibraryBatchScope? batchScope;

    /// <summary>Opens the query surface for one library and local music root.</summary>
    public LiteDbCatalogReader(LiteDbDesktopPlaylistStore store, Guid libraryId, string musicRoot, Func<bool> isOffline, int pageSize = 100,
        LibraryBatchScope? batchScope = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 500);
        if (libraryId == Guid.Empty || !Path.IsPathFullyQualified(musicRoot))
            throw new ArgumentException("A library ID and an absolute music root are required.");
        if (batchScope is { } scope && (scope.LibraryId != libraryId || scope.OriginInstanceId == Guid.Empty))
            throw new ArgumentException("The local batch must belong to this library and an identified desktop.", nameof(batchScope));
        this.store = store;
        this.libraryId = libraryId;
        this.isOffline = isOffline;
        this.pageSize = pageSize;
        this.batchScope = batchScope;
        media = new(musicRoot);
    }

    /// <inheritdoc />
    public Task<Outcome<CatalogPage>> HandleAsync(BrowseCatalog query, RequestContext context, CancellationToken token) =>
        store.ReadAsync(database => Browse(database, query, ReadLocalMaintenance(context.OriginInstanceId), token), token);

    /// <inheritdoc />
    public Task<Outcome<TrackAvailabilitySnapshot>> HandleAsync(GetTrackAvailability query, RequestContext context, CancellationToken token) =>
        store.ReadAsync<TrackAvailabilitySnapshot>(database =>
        {
            var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
            var local = ReadLocalMaintenance(context.OriginInstanceId);
            var result = ImmutableDictionary.CreateBuilder<Guid, TrackAvailability>();
            foreach (var id in query.TrackIds.IsDefault ? [] : query.TrackIds.Distinct())
            {
                token.ThrowIfCancellationRequested();
                result.Add(id, ResolveMedia(tracks, local, id).Availability);
            }
            return new Outcome<TrackAvailabilitySnapshot>.Success(new(Revision(database), result.ToImmutable()));
        }, token);

    /// <inheritdoc />
    public Task<Outcome<PublishedPlaylist>> ReadAsync(Guid playlistId, CancellationToken token) =>
        store.ReadAsync<PublishedPlaylist>(database =>
        {
            var playlist = Find(database.GetCollection<BsonDocument>("catalog_playlists"), playlistId);
            return playlist is null
                ? new Outcome<PublishedPlaylist>.Invalid([new("playlist-not-found", nameof(playlistId))])
                : new Outcome<PublishedPlaylist>.Success(new(new(playlistId, playlist["PlaylistRevision"].AsInt64),
                    playlist["Name"].AsString, PlaylistEntries(playlist)));
        }, token);

    /// <inheritdoc />
    public Task<Outcome<TrackTagSnapshot>> ReadCachedTrackTagsAsync(Guid trackId, CancellationToken token) =>
        store.ReadAsync<TrackTagSnapshot>(database =>
        {
            var projection = new CatalogDocuments(database, libraryId);
            var row = Find(projection.Tracks, trackId);
            if (row is null || !row["SourceRevision"].IsNumber || row["SourceRevision"].AsInt64 <= 0)
                return new Outcome<TrackTagSnapshot>.Invalid([new("cached-track-tags-unavailable")]);

            var album = row["AlbumId"].IsGuid ? Find(projection.Albums, row["AlbumId"].AsGuid) : null;
            var tags = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            Add("title", ReadText(row, "Title"));
            Add("artist", projection.ArtistName(row["ArtistId"]));
            Add("album", row.ContainsKey("TagAlbum") ? ReadText(row, "TagAlbum") : album is null ? null : ReadText(album, "Title"));
            Add("albumartist", row.ContainsKey("TagAlbumArtist") ? ReadText(row, "TagAlbumArtist") :
                album is null ? null : projection.ArtistName(album["AlbumArtistId"]));
            Add("genre", ReadText(row, "Genre"));
            AddNumber("track", "TrackNumber");
            AddNumber("disc", "DiscNumber");
            AddNumber("year", "Year");
            return new Outcome<TrackTagSnapshot>.Success(new(trackId, row["SourceRevision"].AsInt64, tags.ToImmutable()));

            void Add(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                    tags.Add(name, value);
            }

            void AddNumber(string name, string field)
            {
                if (ReadInt(row, field) is > 0 and { } number)
                    tags.Add(name, number.ToString(CultureInfo.InvariantCulture));
            }
        }, token);

    /// <summary>Resolves playback for a visible track from its committed media path.</summary>
    public Task<Outcome<(TrackAvailability Availability, string? FullPath)>> ResolveTrackAsync(Guid trackId, CancellationToken token) =>
        store.ReadAsync<(TrackAvailability, string?)>(database => new Outcome<(TrackAvailability, string?)>.Success(
            ResolveMedia(database.GetCollection<BsonDocument>("catalog_tracks"), ReadLocalMaintenance(batchScope?.OriginInstanceId), trackId)), token);

    private LocalMaintenanceCatalog ReadLocalMaintenance(Guid? origin) => batchScope is { } scope && origin == scope.OriginInstanceId
        ? store.ReadLocalMaintenanceCatalog(scope) : LocalMaintenanceCatalog.Unscoped;

    private (TrackAvailability Availability, string? FullPath) ResolveMedia(ILiteCollection<BsonDocument> tracks, LocalMaintenanceCatalog local, Guid trackId) =>
        local.Includes(trackId) && Find(tracks, trackId) is { } track ? media.Resolve(ReadText(track, "RelativePath")) : (TrackAvailability.Unknown, null);

    /// <summary>Initializes the library and grouping lookups at store open.</summary>
    internal static void EnsureIndexes(ILiteDatabase database)
    {
        foreach (var name in new[] { "catalog_artists", "catalog_albums", "catalog_tracks", "catalog_playlists" })
            database.GetCollection<BsonDocument>(name).EnsureIndex("LibraryId");
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        tracks.EnsureIndex("AlbumId");
        tracks.EnsureIndex("ArtistId");
        database.GetCollection<BsonDocument>("catalog_albums").EnsureIndex("AlbumArtistId");
    }

    private Outcome<CatalogPage> Browse(ILiteDatabase database, BrowseCatalog query, LocalMaintenanceCatalog local, CancellationToken token)
    {
        if (!Enum.IsDefined(query.Category) || !Enum.IsDefined(query.Sort.Key))
            return new Outcome<CatalogPage>.Invalid([new("invalid-catalog-criteria")]);
        var revision = Revision(database);
        var offset = 0;
        if (query.Page is { } page)
        {
            var parsed = ParseCursor(page);
            if (parsed is null || parsed.LibraryId != libraryId || parsed.Category != query.Category || parsed.Filter != query.Filter ||
                parsed.Sort != query.Sort || parsed.Offset <= 0 || parsed.BatchOrigin != local.Origin)
                return new Outcome<CatalogPage>.Invalid([new("invalid-catalog-cursor", nameof(query.Page))]);
            if (parsed.Revision != revision || parsed.BatchRevision != local.Revision)
                return new Outcome<CatalogPage>.Conflict("stale-catalog-cursor", revision);
            offset = parsed.Offset;
        }

        CatalogDocuments snapshot = new(database, libraryId, local);
        var rows = GetRows(snapshot, query.Category, query.Filter, token);
        var sort = query.Category is CatalogCategory.Artists or CatalogCategory.Albums or CatalogCategory.Playlists && query.Sort.Key == CatalogSortKey.TrackNumber
            ? SortValue(CatalogSortKey.Title) : SortValue(query.Sort.Key);
        var ordered = query.Sort.Descending
            ? rows.OrderByDescending(sort, CatalogValueComparer.Instance)
            : rows.OrderBy(sort, CatalogValueComparer.Instance);
        var next = ordered.ThenBy(item => item.Id).Skip(offset).Take(pageSize + 1).ToArray();
        var cursor = next.Length > pageSize
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(libraryId, query.Category, query.Filter, query.Sort,
                revision, checked(offset + pageSize), local.Origin, local.Revision)))
            : null;
        var result = next.Take(pageSize).Select(row => Materialize(snapshot, query.Category, row)).ToImmutableArray();
        return new Outcome<CatalogPage>.Success(new(query.Category, query.Filter, query.Sort, revision, result, cursor, isOffline()));
    }

    private IEnumerable<Row> GetRows(CatalogDocuments documents, CatalogCategory category, CatalogFilter filter, CancellationToken token)
    {
        var text = filter.Text?.Trim();
        var playlistIds = filter.PlaylistId is { } id
            ? PlaylistEntries(Find(documents.Playlists, id)).Select(entry => entry.TrackId).ToHashSet() : null;
        var source = category switch
        {
            CatalogCategory.Artists => documents.Artists,
            CatalogCategory.Albums => documents.Albums,
            CatalogCategory.Playlists => documents.Playlists,
            _ => documents.Tracks
        };
        var predicate = category switch
        {
            CatalogCategory.Artists when filter.ArtistId is { } artist => Query.EQ("_id", artist),
            CatalogCategory.Albums when filter.AlbumId is { } album => Query.EQ("_id", album),
            CatalogCategory.Albums when filter.ArtistId is { } artist => Query.EQ("AlbumArtistId", artist),
            CatalogCategory.Playlists when filter.PlaylistId is { } playlist => Query.EQ("_id", playlist),
            not (CatalogCategory.Artists or CatalogCategory.Albums or CatalogCategory.Playlists) when filter.AlbumId is { } album => Query.EQ("AlbumId", album),
            not (CatalogCategory.Artists or CatalogCategory.Albums or CatalogCategory.Playlists) when filter.ArtistId is { } artist => Query.EQ("ArtistId", artist),
            _ => Query.EQ("LibraryId", libraryId)
        };
        foreach (var doc in source.Find(predicate))
        {
            token.ThrowIfCancellationRequested();
            if (!HasId(doc["LibraryId"], libraryId))
                continue;
            if (category is not (CatalogCategory.Artists or CatalogCategory.Albums or CatalogCategory.Playlists) && !documents.IncludesTrack(doc["_id"].AsGuid))
                continue;
            var row = Project(documents, category, doc);
            if (category == CatalogCategory.Albums && filter.ArtistId is { } albumArtist && !HasId(doc["AlbumArtistId"], albumArtist))
                continue;
            if (category is CatalogCategory.Tracks or CatalogCategory.Library or CatalogCategory.Search &&
                ((filter.ArtistId is { } artist && !HasId(doc["ArtistId"], artist)) || (playlistIds is not null && !playlistIds.Contains(row.Id))))
                continue;
            if (!string.IsNullOrEmpty(text) && !Matches(row.Title, text) && !Matches(row.Artist, text) && !Matches(row.Album, text))
                continue;
            yield return row;
        }
    }

    private Row Project(CatalogDocuments documents, CatalogCategory category, BsonDocument doc)
    {
        var id = doc["_id"].AsGuid;
        if (category is CatalogCategory.Artists or CatalogCategory.Playlists)
        {
            var name = ReadText(doc, "Name") ?? string.Empty;
            return new(id, name, ReadText(doc, "SortName") ?? name, doc);
        }
        var title = ReadText(doc, "Title") ?? string.Empty;
        var artist = documents.ArtistName(doc[category == CatalogCategory.Albums ? "AlbumArtistId" : "ArtistId"]);
        var album = category == CatalogCategory.Albums ? null : documents.AlbumTitle(doc["AlbumId"]);
        return new(id, title, ReadText(doc, "SortTitle") ?? title, doc, artist, album, ReadText(doc, "Genre"),
            ReadInt(doc, "TrackNumber"), ReadInt(doc, "Rating"),
            doc["DurationTicks"].IsNumber ? TimeSpan.FromTicks(doc["DurationTicks"].AsInt64) : null);
    }

    private CatalogRow Materialize(CatalogDocuments documents, CatalogCategory category, Row row)
    {
        var trackIds = category switch
        {
            CatalogCategory.Artists => documents.TracksFor("ArtistId", row.Id),
            CatalogCategory.Albums => documents.TracksFor("AlbumId", row.Id),
            CatalogCategory.Playlists => [.. PlaylistEntries(row.Document).Select(entry => entry.TrackId).Where(documents.IncludesTrack)],
            _ => [row.Id]
        };
        var isTrack = category is not (CatalogCategory.Artists or CatalogCategory.Albums or CatalogCategory.Playlists);
        var committed = isTrack ? ReadText(row.Document, "RelativePath") : null;
        var availability = isTrack ? media.Resolve(committed).Availability : TrackAvailability.Unknown;
        return new(row.Id, row.Title, trackIds, row.Artist, row.Album, row.Genre, row.TrackNumber, row.Rating, row.Duration, availability,
            isTrack ? documents.TrackPath(row.Id, committed) : null);
    }

    private long Revision(ILiteDatabase database) => database.GetCollection<BsonDocument>("sync_checkpoints")
        .FindById($"{libraryId:N}/catalog") is { } checkpoint ? checkpoint["AppliedRevision"].AsInt64 : 0;

    private BsonDocument? Find(ILiteCollection<BsonDocument> collection, Guid id) =>
        collection.FindById(id) is { } row && HasId(row["LibraryId"], libraryId) ? row : null;

    private static ImmutableArray<PlaylistEntry> PlaylistEntries(BsonDocument? doc) => doc?["Entries"].IsArray == true
        ? [.. doc["Entries"].AsArray.Select(value => new PlaylistEntry(value.AsDocument["EntryId"].AsGuid, value.AsDocument["TrackId"].AsGuid))] : [];

    private static string? ReadText(BsonDocument doc, string field) => doc[field].IsString ? doc[field].AsString : null;
    private static int? ReadInt(BsonDocument doc, string field) => doc[field].IsNumber ? doc[field].AsInt32 : null;
    private static bool HasId(BsonValue value, Guid id) => value.IsGuid && value.AsGuid == id;
    private static bool Matches(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static Func<Row, IComparable?> SortValue(CatalogSortKey key) => key switch
    {
        CatalogSortKey.TrackNumber => row => row.TrackNumber,
        CatalogSortKey.Artist => row => row.Artist,
        CatalogSortKey.Album => row => row.Album,
        CatalogSortKey.Genre => row => row.Genre,
        CatalogSortKey.Rating => row => row.Rating,
        CatalogSortKey.Duration => row => row.Duration,
        _ => row => row.SortTitle
    };

    private static Cursor? ParseCursor(string page)
    {
        if (page.Length > 16_384)
            return null;
        try { return JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(page)); }
        catch (Exception exception) when (exception is FormatException or JsonException) { return null; }
    }

    private sealed record Cursor(Guid LibraryId, CatalogCategory Category, CatalogFilter Filter, CatalogSort Sort, long Revision, int Offset,
        Guid? BatchOrigin = null, long BatchRevision = 0);

    private sealed record Row(Guid Id, string Title, string SortTitle, BsonDocument Document, string? Artist = null, string? Album = null,
        string? Genre = null, int? TrackNumber = null, int? Rating = null, TimeSpan? Duration = null);

    private sealed class CatalogValueComparer : IComparer<IComparable?>
    {
        public static CatalogValueComparer Instance { get; } = new();
        public int Compare(IComparable? x, IComparable? y) =>
            x is string left && y is string right ? StringComparer.OrdinalIgnoreCase.Compare(left, right) : Comparer<IComparable?>.Default.Compare(x, y);
    }

    private sealed class CatalogDocuments(ILiteDatabase database, Guid libraryId, LocalMaintenanceCatalog? local = null)
    {
        private readonly Dictionary<Guid, string?> artistNames = [];
        private readonly Dictionary<Guid, string?> albumTitles = [];
        public ILiteCollection<BsonDocument> Artists { get; } = database.GetCollection<BsonDocument>("catalog_artists");
        public ILiteCollection<BsonDocument> Albums { get; } = database.GetCollection<BsonDocument>("catalog_albums");
        public ILiteCollection<BsonDocument> Tracks { get; } = database.GetCollection<BsonDocument>("catalog_tracks");
        public ILiteCollection<BsonDocument> Playlists { get; } = database.GetCollection<BsonDocument>("catalog_playlists");

        public string? ArtistName(BsonValue id) => FindName(artistNames, id, Artists, "Name");
        public string? AlbumTitle(BsonValue id) => FindName(albumTitles, id, Albums, "Title");
        public bool IncludesTrack(Guid trackId) => local?.Includes(trackId) != false;
        public string? TrackPath(Guid trackId, string? committed) => local?.RelativePath(trackId, committed) ?? committed;

        public ImmutableArray<Guid> TracksFor(string field, Guid id) =>
            [.. Tracks.Find(Query.EQ(field, id)).Where(doc => HasId(doc["LibraryId"], libraryId))
                .OrderBy(doc => ReadInt(doc, "TrackNumber")).ThenBy(doc => doc["_id"].AsGuid).Select(doc => doc["_id"].AsGuid).Where(IncludesTrack)];

        private string? FindName(Dictionary<Guid, string?> cache, BsonValue id, ILiteCollection<BsonDocument> collection, string field)
        {
            if (!id.IsGuid)
                return null;
            if (!cache.TryGetValue(id.AsGuid, out var name))
            {
                name = collection.FindById(id) is { } doc && HasId(doc["LibraryId"], libraryId) ? ReadText(doc, field) : null;
                cache.Add(id.AsGuid, name);
            }
            return name;
        }
    }
}
