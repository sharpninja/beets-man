using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;
using static BeetsMan.Desktop.Tests.CatalogAssertions;

namespace BeetsMan.Desktop.IntegrationTests;

public sealed partial class LIBRARYAcceptanceTests
{
    /// <summary>Verifies working-catalog effects and revert against the persisted desktop state and real media files.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 to FR-LIBRARY-002#ac-batch-local and ac-batch-revert for both removal modes.</remarks>
    [Theory]
    [InlineData(RemovalMode.CatalogOnly)]
    [InlineData(RemovalMode.CatalogAndAudio)]
    public async Task LocalMaintenanceProjectsTheWorkingCatalogUntilReverted(RemovalMode removalMode)
    {
        Seed();
        var originalFile = Path.Combine(Music, LocalMaintenanceCatalogAssertions.Original);
        var removedFile = Path.Combine(Music, LocalMaintenanceCatalogAssertions.Removed);
        var proposedFile = Path.Combine(Music, LocalMaintenanceCatalogAssertions.Proposed);
        await File.WriteAllTextAsync(removedFile, "separately retained audio", Token);
        using (var database = new LiteDatabase(DatabasePath))
            database.GetCollection<BsonDocument>("catalog_playlists").Insert(new BsonDocument
                { ["_id"] = Guid.NewGuid(), ["LibraryId"] = Library, ["Name"] = "Radio", ["PlaylistRevision"] = 1L });

        var scope = new LibraryBatchScope(Library, Guid.NewGuid());
        var other = new LibraryBatchScope(Library, Guid.NewGuid());
        var local = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var foreign = new RequestContext(Guid.NewGuid(), other.OriginInstanceId);
        var trackQuery = Queries[2];
        var artistQuery = Queries[0];
        var albumQuery = Queries[1];
        var playlistQuery = Queries[3];
        var search = new BrowseCatalog(CatalogCategory.Search, new(Text: "Second"), new(CatalogSortKey.Title));
        var allPlaylists = new BrowseCatalog(CatalogCategory.Playlists, new(), new(CatalogSortKey.Title));
        var move = new FileMove(LocalMaintenanceCatalogAssertions.Original, LocalMaintenanceCatalogAssertions.Proposed, Downloaded);
        var organize = new OrganizePlan(Guid.NewGuid(), Library, Revision, [move], [Downloaded]);
        var remove = new RemovalPlan(Guid.NewGuid(), Library, Revision, removalMode, [Pending],
            removalMode is RemovalMode.CatalogAndAudio ? [LocalMaintenanceCatalogAssertions.Removed] : []);
        CatalogPage tracks;
        CatalogPage searched;
        CatalogPage artists;
        CatalogPage albums;
        CatalogPage playlists;
        CatalogPage another;
        TrackAvailabilitySnapshot availability;
        (TrackAvailability Availability, string? FullPath) playback;
        (TrackAvailability Availability, string? FullPath) removedPlayback;
        string beforeStage;
        string beforeRevert;
        string localCursor;
        LibraryBatchReview pending;

        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: false))
        {
            var batch = new LibraryBatchHandlers(scope, store, new UnexpectedCatalogBatchPublisher());
            var reader = Reader(store, scope);
            var paged = Reader(store, scope, 1);
            var unmodified = Success(await paged.HandleAsync(trackQuery, local, Token));
            Assert.Equal(LocalMaintenanceCatalogAssertions.Original, Assert.Single(unmodified.Rows).RelativePath);
            Assert.NotNull(unmodified.NextPage);
            Assert.Equal((TrackAvailability.Downloaded, Path.GetFullPath(removedFile)), Success(await reader.ResolveTrackAsync(Pending, Token)));
            Success(await batch.HandleAsync(new SetLibraryBatchMode(true, 0), Operation(), Token));
            Success(await batch.StageAsync(new LibraryBatchChange.Organize(organize), Operation(), Token));
            Success(await batch.StageAsync(new LibraryBatchChange.Remove(remove, new(remove.PlanId, removalMode)), Operation(), Token));
            pending = Success(await batch.HandleAsync(new GetLibraryBatchReview(), local, Token));
            Assert.Equal(2, pending.Entries.Length);

            beforeStage = Assert.IsType<Outcome<CatalogPage>.Conflict>(await paged.HandleAsync(trackQuery with { Page = unmodified.NextPage }, local, Token)).Code;
            localCursor = Success(await paged.HandleAsync(allPlaylists, local, Token)).NextPage!;
            Assert.NotNull(localCursor);
            Assert.IsType<Outcome<CatalogPage>.Invalid>(await paged.HandleAsync(allPlaylists with { Page = localCursor }, foreign, Token));
            tracks = Success(await reader.HandleAsync(trackQuery, local, Token));
            searched = Success(await reader.HandleAsync(search, local, Token));
            artists = Success(await reader.HandleAsync(artistQuery, local, Token));
            albums = Success(await reader.HandleAsync(albumQuery, local, Token));
            playlists = Success(await reader.HandleAsync(playlistQuery, local, Token));
            var filtered = Success(await reader.HandleAsync(trackQuery with { Filter = new(PlaylistId: Playlist) }, local, Token));
            Assert.Equal([Downloaded], filtered.Rows.Select(item => item.Id));
            another = Success(await Reader(store, other).HandleAsync(trackQuery, foreign, Token));
            Assert.Equal(LocalMaintenanceCatalogAssertions.Original, another.Rows[0].RelativePath);
            Assert.Equal([Downloaded, Pending], Success(await reader.HandleAsync(trackQuery, foreign, Token)).Rows.Select(item => item.Id));
            availability = Success(await reader.HandleAsync(new GetTrackAvailability([Downloaded, Pending]), local, Token));
            Assert.Equal(TrackAvailability.Downloaded, availability.Tracks[Downloaded]);
            playback = Success(await reader.ResolveTrackAsync(Downloaded, Token));
            removedPlayback = Success(await reader.ResolveTrackAsync(Pending, Token));
            Assert.Equal([Pending, Downloaded, Downloaded], Success(await reader.ReadAsync(Playlist, Token)).Entries.Select(entry => entry.TrackId));
        }

        bool survivedRestart;
        CatalogPage reverted;
        using (var reopened = LiteDbDesktopPlaylistStore.Open(DatabasePath, allowCreate: false))
        {
            var reader = Reader(reopened, scope);
            var afterRestart = Assert.Single(Success(await reader.HandleAsync(trackQuery, local, Token)).Rows);
            survivedRestart = afterRestart.Id == Downloaded && afterRestart.RelativePath == LocalMaintenanceCatalogAssertions.Proposed &&
                Success(await reader.ResolveTrackAsync(Pending, Token)).FullPath is null;
            var batch = new LibraryBatchHandlers(scope, reopened, new UnexpectedCatalogBatchPublisher());
            Success(await batch.HandleAsync(new RevertLibraryBatch(pending.Version, KeepModeEnabled: false), Operation(), Token));
            beforeRevert = Assert.IsType<Outcome<CatalogPage>.Conflict>(await Reader(reopened, scope, 1).HandleAsync(
                allPlaylists with { Page = localCursor }, local, Token)).Code;
            reverted = Success(await reader.HandleAsync(trackQuery, local, Token));
            Assert.Equal((TrackAvailability.Downloaded, Path.GetFullPath(removedFile)), Success(await reader.ResolveTrackAsync(Pending, Token)));
            Assert.Equal([Pending, Downloaded, Downloaded], Assert.Single(Success(await reader.HandleAsync(playlistQuery, local, Token)).Rows).TrackIds);
        }

        using var snapshot = new LiteDatabase(DatabasePath);
        var original = snapshot.GetCollection<BsonDocument>("catalog_tracks").FindById(Downloaded);
        var removed = snapshot.GetCollection<BsonDocument>("catalog_tracks").FindById(Pending);
        var intact = original["RelativePath"].AsString == LocalMaintenanceCatalogAssertions.Original &&
            removed["RelativePath"].AsString == LocalMaintenanceCatalogAssertions.Removed && !File.Exists(proposedFile) &&
            File.ReadAllText(originalFile) == "local" && File.ReadAllText(removedFile) == "separately retained audio";
        var approved = Assert.Single(tracks.Rows);
        LocalMaintenanceCatalogAssertions.Verify(new([.. tracks.Rows.Select(row => row.Title)], [.. searched.Rows.Select(row => row.Title)],
            Titles(Assert.Single(artists.Rows)), Titles(Assert.Single(albums.Rows)), Titles(Assert.Single(playlists.Rows)), approved.RelativePath,
            playback.Availability, Relative(playback.FullPath), availability.Tracks[Pending], Relative(removedPlayback.FullPath),
            tracks.Revision == Revision && availability.Revision == Revision && reverted.Revision == Revision, beforeStage, beforeRevert,
            [.. another.Rows.Select(row => row.Title)], survivedRestart, [.. reverted.Rows.Select(row => row.Title)], reverted.Rows[0].RelativePath, intact));

        CommandContext Operation() => new(local, Guid.NewGuid());
        LiteDbCatalogReader Reader(LiteDbDesktopPlaylistStore store, LibraryBatchScope owner, int limit = 100) =>
            new(store, Library, Music, () => true, pageSize: limit, batchScope: owner);
        static string[] Titles(CatalogRow row) => [.. row.TrackIds.Select(id => id == Downloaded ? "First" : "Second")];
        string? Relative(string? fullPath) => fullPath is null ? null : Path.GetRelativePath(Music, fullPath).Replace('\\', '/');
        static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    private sealed class UnexpectedCatalogBatchPublisher : IApprovedLibraryBatchPublisher
    {
        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview review, Guid operationId,
            CancellationToken token) => throw new InvalidOperationException("Local maintenance must not publish on stage or revert.");
    }
}
