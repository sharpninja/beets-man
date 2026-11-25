using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using LiteDB;
using Xunit;
using static BeetsMan.Desktop.Tests.CatalogAssertions;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Checks the library catalog against a populated LiteDB projection and real offline files.</summary>
/// <remarks>2026-11-24: Bind TEST-LIBRARY-001/002 for FR-LIBRARY-001#ac-browse and ac-offline.</remarks>
public sealed class CatalogIntegrationTests : IDisposable
{
    private static readonly Guid Library = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherLibrary = Guid.Parse("60000000-0000-0000-0000-000000000002");
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-man-catalog-{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(root, "catalog.db");
    private string Music => Path.Combine(root, "Music");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly RequestContext context = new(Guid.NewGuid(), null);

    [Fact]
    public async Task BrowsesAllOfflineCategoriesAndResolvesOnlyOwnedDownloadedMedia()
    {
        Seed();
        using var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: false);
        var reader = new LiteDbCatalogReader(store, Library, Music, () => true);
        var pages = ImmutableArray.CreateBuilder<CatalogPage>();
        foreach (var query in Queries)
            pages.Add(Assert.IsType<Outcome<CatalogPage>.Success>(await reader.HandleAsync(query, context, Token)).Value);
        VerifyPages(pages.ToImmutable());
        var statuses = await reader.HandleAsync(new GetTrackAvailability([Downloaded, Pending]), context, Token);
        VerifyAvailability(Assert.IsType<Outcome<TrackAvailabilitySnapshot>.Success>(statuses).Value);

        var file = Assert.IsType<Outcome<(TrackAvailability, string?)>.Success>(await reader.ResolveTrackAsync(Downloaded, Token)).Value;
        Assert.Equal(TrackAvailability.Downloaded, file.Item1);
        Assert.Equal(Path.Combine(Music, "Set", "first.flac"), file.Item2);
        var pending = Assert.IsType<Outcome<(TrackAvailability, string?)>.Success>(await reader.ResolveTrackAsync(Pending, Token)).Value;
        Assert.Equal((TrackAvailability.Pending, (string?)null), pending);
        var foreign = Assert.IsType<Outcome<(TrackAvailability, string?)>.Success>(await reader.ResolveTrackAsync(OtherLibrary, Token)).Value;
        Assert.Equal((TrackAvailability.Unknown, (string?)null), foreign);
        var playlist = Assert.IsType<Outcome<PublishedPlaylist>.Success>(await reader.ReadAsync(Playlist, Token)).Value;
        Assert.Equal(3, playlist.Version.Revision);
        Assert.Equal([Pending, Downloaded, Downloaded], playlist.Entries.Select(entry => entry.TrackId));
    }

    [Fact]
    public async Task PagesInStableOrderAndRejectsChangedProjectionOrCriteria()
    {
        Seed();
        var query = Queries[2];
        string nextPage;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: false))
        {
            var reader = new LiteDbCatalogReader(store, Library, Music, () => true, pageSize: 1);
            var first = Assert.IsType<Outcome<CatalogPage>.Success>(await reader.HandleAsync(query, context, Token)).Value;
            Assert.Equal(Downloaded, Assert.Single(first.Rows).Id);
            Assert.NotNull(first.NextPage);
            nextPage = first.NextPage;
            var second = Assert.IsType<Outcome<CatalogPage>.Success>(await reader.HandleAsync(query with { Page = nextPage }, context, Token)).Value;
            Assert.Equal(Pending, Assert.Single(second.Rows).Id);
            Assert.Null(second.NextPage);
            Assert.IsType<Outcome<CatalogPage>.Invalid>(await reader.HandleAsync(query with
            {
                Page = nextPage, Sort = new(CatalogSortKey.Title)
            }, context, Token));
        }

        using (var database = new LiteDatabase(DatabasePath))
            WriteCheckpoint(database, Revision + 1);
        using var reopened = LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: false);
        var resumed = new LiteDbCatalogReader(reopened, Library, Music, () => true, pageSize: 1);
        VerifyCursorConflict(await resumed.HandleAsync(query with { Page = nextPage }, context, Token), Revision + 1);
    }

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Music, "Set"));
        File.WriteAllText(Path.Combine(Music, "Set", "first.flac"), "local");
        using (LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: true)) { }
        using var database = new LiteDatabase(DatabasePath);
        database.GetCollection<BsonDocument>("catalog_artists").Insert(
            new BsonDocument { ["_id"] = Artist, ["LibraryId"] = Library, ["Name"] = "Violet", ["SortName"] = "Violet" });
        database.GetCollection<BsonDocument>("catalog_albums").Insert(
            new BsonDocument { ["_id"] = Album, ["LibraryId"] = Library, ["AlbumArtistId"] = Artist, ["Title"] = "Set", ["SortTitle"] = "Set" });
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        tracks.Insert(Track(Downloaded, Library, "First", 1, "Set/first.flac"));
        tracks.Insert(Track(Pending, Library, "Second", 2, "Set/missing.flac"));
        tracks.Insert(Track(OtherLibrary, OtherLibrary, "Violet", 0, "../outside.flac"));
        database.GetCollection<BsonDocument>("catalog_playlists").Insert(
            new BsonDocument { ["_id"] = Playlist, ["LibraryId"] = Library, ["Name"] = "Evening", ["PlaylistRevision"] = 3L,
                ["Entries"] = new BsonArray(new[] { Pending, Downloaded, Downloaded }.Select(id => (BsonValue)new BsonDocument
                {
                    ["EntryId"] = Guid.NewGuid(), ["TrackId"] = id
                })) });
        WriteCheckpoint(database, Revision);
    }

    private static BsonDocument Track(Guid id, Guid library, string title, int number, string relativePath) =>
        new() { ["_id"] = id, ["LibraryId"] = library, ["ArtistId"] = Artist, ["AlbumId"] = Album,
            ["Title"] = title, ["TrackNumber"] = number, ["RelativePath"] = relativePath };

    private static void WriteCheckpoint(ILiteDatabase database, long revision) =>
        database.GetCollection<BsonDocument>("sync_checkpoints").Upsert(
            new BsonDocument { ["_id"] = $"{Library:N}/catalog", ["LibraryId"] = Library, ["Feed"] = "catalog", ["AppliedRevision"] = revision });

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
