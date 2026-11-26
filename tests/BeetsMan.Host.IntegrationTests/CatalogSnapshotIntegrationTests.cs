using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Rebuilds one expired replica from a pinned capture after both endpoints restart.</summary>
    /// <remarks>2026-11-24: Bind TEST-RECORDS-002 to expiry, consistent replacement and retained local intent for FR-RECORDS-001#ac-replicate, ac-notify and ac-offline.</remarks>
    [Fact]
    public async Task ExpiredReplicaKeepsPrivateStateAndRecoversPinnedSnapshotAfterRestart()
    {
        const string peerToken = "snapshot-recipient-fixture";
        const string otherToken = "snapshot-other-fixture";
        var replica = Guid.NewGuid();
        var other = Guid.NewGuid();
        var otherLibrary = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var album = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        var old = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var hostDb = Path.Combine(root, "snapshot-host.db");
        var localDb = Path.Combine(root, "snapshot-local.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var localAudio = Path.Combine(music, "Old.flac");
        await File.WriteAllTextAsync(localAudio, "original audio", Token);
        var draftEntry = new PlaylistEntry(Guid.NewGuid(), old);
        var draft = new PlaylistDraft(Guid.NewGuid(), 4, "Offline edit", [draftEntry], new(playlist, 1));
        var pending = new PendingPlaylistSave(new(replica, Guid.NewGuid()), draft.DraftId, draft.Revision, draft.BasePlaylist, draft.Name,
            draft.Entries, PendingPlaylistSaveStatus.Conflicted, ConflictCode: "stale-playlist", ActualHostRevision: 2);
        var scope = new LibraryBatchScope(library, replica);
        var request = new RequestContext(Guid.NewGuid(), replica);
        var snapshotOperation = new CommandContext(request, Guid.NewGuid());
        var snapshot = new ResumeReplication(library, replica, ReplicaRecoveryMode.Snapshot, 2);

        SeedHost(hostDb);
        using (var db = new LiteDatabase(hostDb))
        {
            foreach (var peer in new[] { replica, other })
                db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                    { ["_id"] = peer, ["LibraryId"] = library, ["Status"] = "Active", ["Role"] = "Desktop" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument
                { ["_id"] = library, ["CurrentRevision"] = 7L, ["MinRetainedRevision"] = 5L });
            db.GetCollection<BsonDocument>("catalog_artists").Insert(new BsonDocument
                { ["_id"] = artist, ["LibraryId"] = library, ["Revision"] = 5L, ["Name"] = "Artist" });
            db.GetCollection<BsonDocument>("catalog_albums").Insert(new BsonDocument
                { ["_id"] = album, ["LibraryId"] = library, ["Revision"] = 6L, ["Title"] = "Set", ["AlbumArtistId"] = artist });
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = survivor, ["LibraryId"] = library, ["Revision"] = 6L, ["Title"] = "Survivor", ["RelativePath"] = "Survivor.flac",
                    ["ArtistId"] = artist, ["AlbumId"] = album });
            db.GetCollection<BsonDocument>("saved_playlists").Insert(new BsonDocument
                { ["_id"] = playlist, ["LibraryId"] = library, ["Revision"] = 1L, ["Name"] = "Captured", ["Entries"] = new BsonArray() });
        }
        using (LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: true)) { }
        using (var db = new LiteDatabase(localDb))
        {
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = old, ["LibraryId"] = library, ["Title"] = "Old", ["RelativePath"] = "Old.flac" });
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = unrelated, ["LibraryId"] = otherLibrary, ["Title"] = "Foreign", ["RelativePath"] = "Foreign.flac" });
            db.GetCollection<BsonDocument>("sync_checkpoints").Insert(new BsonDocument
                { ["_id"] = $"{library:N}/catalog", ["LibraryId"] = library, ["AppliedRevision"] = 2L });
        }
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        { [secret] = new(library, instance), [peerToken] = new(library, replica), [otherToken] = new(library, other) });
        var edited = new PlaylistSaveRequest(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), playlist, 1, "Later",
            [new(Guid.NewGuid(), survivor)]);
        bool expired;
        long partialCursor;
        string[] partialTracks;
        long[] upperPages;
        int earlyAck;
        bool denied;
        using (var store = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: false))
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var origin = Client(address, secret);
            using var foreign = Client(address, otherToken);
            using var transport = new SnapshotTestTransport(CreateClientHandler(certificate), true, async () =>
            {
                using var response = await origin.PostAsJsonAsync(PlaylistPublicationRoute.Path(library), edited, Token);
                response.EnsureSuccessStatusCode();
                Assert.Equal(8, (await response.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token))!.FeedRevision);
            });
            using var http = new HttpClient(transport);
            var dispatcher = Routes(store, http, address);
            Success(await store.ExecuteAsync<bool>(repos =>
            {
                repos.Drafts.Add(draft);
                repos.Operations.Add(pending);
                return new Outcome<bool>.Success(true);
            }, Token));
            var batches = new LibraryBatchHandlers(scope, store, new UnexpectedSnapshotBatchPublisher());
            Success(await batches.HandleAsync(new SetLibraryBatchMode(true, 0), new(request, Guid.NewGuid()), Token));
            Success(await batches.StageAsync(new LibraryBatchChange.TrackTags(old, 2, ImmutableSortedDictionary<string, string>.Empty,
                ImmutableSortedDictionary<string, string?>.Empty.Add("genre", "Draft")), new(request, Guid.NewGuid()), Token));
            var state = Success(await dispatcher.SendAsync(snapshot with { RecoveryMode = ReplicaRecoveryMode.Incremental }, new(request, Guid.NewGuid()), Token));
            expired = state.SnapshotRequired && state.Cursor == 2;
            var failure = Assert.IsType<Outcome<ReplicationState>.DependencyFailure>(await dispatcher.SendAsync(snapshot, snapshotOperation, Token));
            Assert.Equal("catalog-snapshot-page-host-unreachable", failure.Code);
            partialCursor = Success(await dispatcher.QueryAsync(new GetReplicationStatus(library, replica), request, Token)).Cursor;
            partialTracks = await Titles(store);
            upperPages = [.. transport.Uppers];
            earlyAck = transport.Acks.Count;
            using var cross = await foreign.GetAsync(CatalogSnapshotRoutes.PagePath(library, snapshotOperation.OperationId, 0, 1), Token);
            denied = cross.StatusCode == HttpStatusCode.Conflict;
        }

        using (var staged = new LiteDatabase(localDb))
        {
            Assert.Equal(1, staged.GetCollection<BsonDocument>("catalog_snapshot_stage_rows").Count());
            Assert.Equal(1, staged.GetCollection<BsonDocument>("catalog_snapshot_stages").Count());
        }

        long snapshotCursor;
        long caughtUp;
        long capturedPlaylist;
        long newerPlaylist;
        string[] snapshotTracks;
        long[] acknowledged;
        bool preserved;
        using (var store = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: false))
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var transport = new SnapshotTestTransport(CreateClientHandler(certificate), false, null);
            using var http = new HttpClient(transport);
            var dispatcher = Routes(store, http, address);
            var replaced = Success(await dispatcher.SendAsync(snapshot, snapshotOperation, Token));
            Assert.Equal(snapshotOperation.OperationId, replaced.RecoveryOperationId);
            Assert.False(replaced.SnapshotRequired);
            snapshotCursor = replaced.Cursor;
            snapshotTracks = await Titles(store);
            var reader = new LiteDbCatalogReader(store, library, music, () => false);
            Assert.Equal(6, Success(await reader.ReadCachedTrackTagsAsync(survivor, Token)).CatalogRevision);
            capturedPlaylist = Success(await reader.ReadAsync(playlist, Token)).Version.Revision;
            using var direct = Client(address, peerToken);
            var pendingAfterSnapshot = await direct.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token);
            Assert.Equal([8L], pendingAfterSnapshot!.PendingRevisions);
            caughtUp = Success(await dispatcher.SendAsync(new ResumeReplication(library, replica, ReplicaRecoveryMode.Incremental, snapshotCursor),
                new(request, Guid.NewGuid()), Token)).Cursor;
            var after = Success(await reader.ReadAsync(playlist, Token));
            newerPlaylist = after.Version.Revision;
            Assert.Equal(edited.Entries[0].EntryId, Assert.Single(after.Entries).EntryId);
            acknowledged = [.. transport.Acks];
            upperPages = [.. upperPages, .. transport.Uppers];

            var batches = new LibraryBatchHandlers(scope, store, new UnexpectedSnapshotBatchPublisher());
            var batch = Success(await batches.HandleAsync(new GetLibraryBatchReview(), request, Token));
            var saved = Success(await store.ReadDraftAsync(draft.DraftId, Token));
            var operation = Success(await store.ExecuteAsync<PendingPlaylistSave?>(repos => new Outcome<PendingPlaylistSave?>.Success(repos.Operations.Find(pending.Key)), Token));
            preserved = saved?.Name == draft.Name && saved.Revision == draft.Revision && saved.Entries.SequenceEqual(draft.Entries) &&
                operation?.Status == PendingPlaylistSaveStatus.Conflicted && operation.ConflictCode == pending.ConflictCode &&
                operation.Key == pending.Key && operation.Entries.SequenceEqual(pending.Entries) &&
                Assert.IsType<LibraryBatchChange.TrackTags>(Assert.Single(batch.Entries).Change).TrackId == old;
        }
        using var recovered = new LiteDatabase(localDb);
        var intact = recovered.GetCollection<BsonDocument>("catalog_tracks").FindById(unrelated)["LibraryId"].AsGuid == otherLibrary &&
            await File.ReadAllTextAsync(localAudio, Token) == "original audio";
        Assert.Empty(recovered.GetCollection<BsonDocument>("catalog_snapshot_stage_rows").FindAll());
        Assert.Empty(recovered.GetCollection<BsonDocument>("catalog_snapshot_stages").FindAll());
        CatalogSnapshotAssertions.Verify(new(expired, partialCursor, partialTracks, earlyAck, upperPages, snapshotCursor, capturedPlaylist, snapshotTracks,
            caughtUp, newerPlaylist, acknowledged, preserved, intact, denied));

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, Path.Combine(root, "private"), Path.Combine(root, "work"), []),
                verifier, credentials, credentials);
        }
        HttpClient Client(Uri address, string value)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
            return client;
        }
        RequestDispatcher Routes(LiteDbDesktopPlaylistStore store, HttpClient http, Uri address) => new CatalogReplicaHandlers(library, replica,
            new HttpCatalogReplicaHost(http, address, library, replica, new TokenProvider(library, replica, peerToken), pageSize: 1), store).Register(new()).Build();
        async Task<string[]> Titles(LiteDbDesktopPlaylistStore store) =>
            [.. Success(await new LiteDbCatalogReader(store, library, music, () => false).HandleAsync(new BrowseCatalog(CatalogCategory.Tracks, new(),
                new(CatalogSortKey.Title)), request, Token)).Rows.Select(row => row.Title)];
        static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class SnapshotTestTransport(HttpMessageHandler inner, bool interrupt, Func<Task>? afterCapture) : DelegatingHandler(inner)
    {
        public List<long> Uppers { get; } = [];
        public List<long> Acks { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var snapshot = path.Contains("/catalog/snapshots", StringComparison.Ordinal);
            if (snapshot && request.Method == HttpMethod.Get && interrupt)
                throw new HttpRequestException("Interrupted snapshot download.");
            if (path.EndsWith("/acknowledge", StringComparison.Ordinal))
                Acks.Add((await request.Content!.ReadFromJsonAsync<CatalogAcknowledgement>(token))!.ThroughRevision);
            var response = await base.SendAsync(request, token);
            if (snapshot && response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                Uppers.Add(JsonSerializer.Deserialize<CatalogSnapshotPage>(bytes, JsonSerializerOptions.Web)!.UpperRevision);
                if (request.Method == HttpMethod.Post && afterCapture is not null)
                    await afterCapture();
            }
            return response;
        }
    }

    private sealed class UnexpectedSnapshotBatchPublisher : IApprovedLibraryBatchPublisher
    {
        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview review, Guid operationId,
            CancellationToken token) => throw new InvalidOperationException("Snapshot recovery cannot publish a local batch.");
    }
}
