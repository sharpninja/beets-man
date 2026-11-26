using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Api;
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
    /// <summary>Exercises an authenticated catalog pull, local transaction rollback, and independent peer delivery through real stores.</summary>
    /// <remarks>2026-11-24: Bind TEST-RECORDS-002 for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
    /// <remarks>2026-11-26: Trace independent acknowledgment and lost-reply recovery to FR-RECORDS-001#ac-notify.</remarks>
    internal async Task ReplicaCommitsFixedWindowBeforeAcknowledgingAndResumesAfterLostAck()
    {
        const string peerCredential = "catalog-test-peer";
        const string offlineCredential = "catalog-test-offline";
        var online = Guid.NewGuid();
        var offline = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var foreignTrack = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var album = Guid.NewGuid();
        var old = Guid.NewGuid();
        var keep = Guid.NewGuid();
        var hostDatabase = Path.Combine(root, "catalog-feed-host.db");
        var localDatabase = Path.Combine(root, "catalog-feed-local.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var oldFile = Path.Combine(music, "Old.flac");
        await File.WriteAllTextAsync(oldFile, "retained audio", Token);
        SeedHost(hostDatabase, revokedInstance: revoked);
        using (var db = new LiteDatabase(hostDatabase))
        {
            var instances = db.GetCollection<BsonDocument>("registered_instances");
            foreach (var peer in new[] { online, offline })
                instances.Insert(new BsonDocument { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 2L });
            var artistRow = Row(artist, 1);
            artistRow["Name"] = "Foundations";
            var albumRow = Row(album, 1);
            albumRow["Title"] = "Selections";
            albumRow["AlbumArtistId"] = artist;
            var keepRow = Track(keep, "Keep");
            var oldRow = Track(old, "Old");
            AddRevision(1, Change("Artist", "Upsert", artistRow), Change("Album", "Upsert", albumRow),
                Change("Track", "Upsert", oldRow), Change("Track", "Upsert", keepRow));
            AddRevision(2, Change("Track", "Delete", Row(old, 2)));
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(keepRow);

            void AddRevision(long revision, params BsonDocument[] changes)
            {
                db.GetCollection<BsonDocument>("revision_changes").Insert(new BsonDocument
                {
                    ["_id"] = $"{library:N}/{revision}", ["LibraryId"] = library, ["Revision"] = revision, ["OriginInstanceId"] = instance,
                    ["OperationId"] = Guid.NewGuid(), ["Changes"] = new BsonArray(changes.Cast<BsonValue>())
                });
                foreach (var peer in new[] { online, offline })
                    db.GetCollection<BsonDocument>("notification_deliveries").Insert(new BsonDocument
                    {
                        ["_id"] = $"{library:N}/{revision}/{peer:N}", ["LibraryId"] = library, ["Revision"] = revision,
                        ["RecipientInstanceId"] = peer, ["Status"] = "Pending"
                    });
            }
        }
        using (LiteDbDesktopPlaylistStore.Open(localDatabase, allowCreate: true)) { }
        using (var db = new LiteDatabase(localDatabase))
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = foreignTrack, ["LibraryId"] = Guid.NewGuid(), ["Title"] = "Other library" });

        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        {
            [secret] = new(library, instance), [peerCredential] = new(library, online), [offlineCredential] = new(library, offline),
            [RevokedCredential] = new(library, revoked)
        });
        await using var host = LibraryHostStartup.Build(builder, new(library, hostDatabase, music, Path.Combine(root, "private"),
            Path.Combine(root, "work"), []), verifier, credentials, credentials);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var origin = Client(secret);
        using var peerClient = Client(peerCredential);
        using var disconnected = Client(offlineCredential);
        using var revokedClient = Client(RevokedCredential);

        var save = new PlaylistSaveRequest(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Twice",
            [new(Guid.NewGuid(), keep), new(Guid.NewGuid(), keep)]);
        PlaylistSaveResponse? published = null;
        var context = new RequestContext(Guid.NewGuid(), online);
        var resume = new ResumeReplication(library, online, ReplicaRecoveryMode.Incremental, 0);
        var operation = new CommandContext(context, Guid.NewGuid());
        CatalogFeedPage originalPage;
        long committedBeforeAck;
        long recoveredCursor;
        long[] windows;
        int[] attempts;
        string failure;
        bool rolledBack;
        string[] titles;
        string[] playlistEntries;
        long playlistRevision;
        bool remainedDeleted;
        using (var store = LiteDbDesktopPlaylistStore.Open(localDatabase, allowCreate: false))
        {
            using var transport = Transport(async () =>
            {
                using var response = await origin.PostAsJsonAsync(PlaylistPublicationRoute.Path(library), save, Token);
                response.EnsureSuccessStatusCode();
                published = (await response.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token))!;
                Assert.Equal(3, published.FeedRevision);
            }, async through =>
            {
                var durable = Success(await store.ReadReplicaAsync(library, online, Token));
                Assert.Equal(through, durable.Cursor);
            }, dropFirstAck: true);
            using var http = new HttpClient(transport);
            var dispatcher = Handlers(store, http);
            failure = Assert.IsType<Outcome<ReplicationState>.DependencyFailure>(await dispatcher.SendAsync(resume, operation, Token)).Code;
            committedBeforeAck = Success(await dispatcher.QueryAsync(new GetReplicationStatus(library, online), context, Token)).Cursor;
            var pendingWhileOffline = await Pending(peerClient);
            Assert.Equal([1L, 2L, 3L], pendingWhileOffline);
            originalPage = transport.Pages[0];
            windows = [.. transport.Pages.Select(page => page.UpperRevision)];
            attempts = [.. transport.Acknowledgements];
        }

        using (var reopened = LiteDbDesktopPlaylistStore.Open(localDatabase, allowCreate: false))
        using (var transport = Transport(null, null, dropFirstAck: false))
        using (var http = new HttpClient(transport))
        {
            var dispatcher = Handlers(reopened, http);
            Assert.Equal(committedBeforeAck, Success(await dispatcher.QueryAsync(new GetReplicationStatus(library, online), context, Token)).Cursor);
            recoveredCursor = Success(await dispatcher.SendAsync(resume, operation, Token)).Cursor;
            Assert.Equal(recoveredCursor, Success(await reopened.ApplyCatalogPageAsync(originalPage, Token)).Cursor);
            windows = [.. windows, .. transport.Pages.Select(page => page.UpperRevision)];
            attempts = [.. attempts, .. transport.Acknowledgements];
            var reader = new LiteDbCatalogReader(reopened, library, music, () => false);
            var query = new BrowseCatalog(CatalogCategory.Tracks, new(), new(CatalogSortKey.Title));
            var altered = new ReplicaTrack("Tampered", "Keep.flac", artist, album, null, null, null, null, null, null, null, false, null, false, null);
            var tampered = new CatalogFeedPage(library, online, 3, 4, 4, [new(4, instance, Guid.NewGuid(),
                [new(CatalogEntityKind.Track, CatalogEntityOperation.Upsert, keep, altered), new(CatalogEntityKind.Track, CatalogEntityOperation.Delete, foreignTrack)])]);
            rolledBack = Assert.IsType<Outcome<ReplicationState>.Conflict>(await reopened.ApplyCatalogPageAsync(tampered, Token)).Code == "catalog-record-scope-conflict" &&
                Success(await reopened.ReadReplicaAsync(library, online, Token)).Cursor == 3;
            titles = [.. Success(await reader.HandleAsync(query, context, Token)).Rows.Select(row => row.Title)];
            var keepTag = Success(await reader.ReadCachedTrackTagsAsync(keep, Token));
            Assert.Equal(1, keepTag.CatalogRevision);
            Assert.Equal("Selections", keepTag.Tags["album"]);
            Assert.Equal("Foundations", keepTag.Tags["artist"]);
            var playlist = Success(await reader.ReadAsync(published!.PlaylistId, Token));
            playlistEntries = [.. playlist.Entries.Select(entry => entry.TrackId == keep ? "Keep" : "Other")];
            Assert.Equal(save.Entries.Select(entry => entry.EntryId), playlist.Entries.Select(entry => entry.EntryId));
            playlistRevision = playlist.Version.Revision;
            remainedDeleted = Success(await reader.ResolveTrackAsync(old, Token)).Availability == TrackAvailability.Unknown;
        }
        using var foreignAck = await peerClient.PostAsJsonAsync(CatalogReplicationRoutes.AcknowledgePath(library), new CatalogAcknowledgement(offline, 3), Token);
        using var forbidden = await revokedClient.GetAsync(CatalogReplicationRoutes.ChangesPath(library, 0, null, 1), Token);
        using var badCursor = await peerClient.GetAsync(CatalogReplicationRoutes.ChangesPath(library, 4, null, 1), Token);
        CatalogReplicationAssertions.Verify(new(windows, committedBeforeAck, recoveredCursor, attempts, titles, playlistEntries, playlistRevision,
            remainedDeleted, await Pending(origin), await Pending(peerClient), await Pending(disconnected), failure, rolledBack,
            foreignAck.StatusCode == HttpStatusCode.Forbidden && forbidden.StatusCode == HttpStatusCode.Forbidden && badCursor.StatusCode == HttpStatusCode.Conflict,
            await File.ReadAllTextAsync(oldFile, Token) == "retained audio"));

        BsonDocument Row(Guid id, long revision) => new() { ["_id"] = id, ["LibraryId"] = library, ["Revision"] = revision };
        BsonDocument Track(Guid id, string title)
        {
            var row = Row(id, 1);
            row["Title"] = title;
            row["RelativePath"] = title + ".flac";
            row["ArtistId"] = artist;
            row["AlbumId"] = album;
            return row;
        }
        static BsonDocument Change(string kind, string kindOfChange, BsonDocument row) => new()
            { ["Version"] = 1, ["Kind"] = kind, ["Operation"] = kindOfChange, ["Payload"] = row };
        HttpClient Client(string credential)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return client;
        }
        ReplicaCatalogTransport Transport(Func<Task>? publish, Func<long, Task>? beforeAck, bool dropFirstAck) =>
            new(CreateClientHandler(certificate), publish, beforeAck, dropFirstAck);
        RequestDispatcher Handlers(LiteDbDesktopPlaylistStore store, HttpClient client) => new CatalogReplicaHandlers(library, online,
            new HttpCatalogReplicaHost(client, address, library, online, new TokenProvider(library, online, peerCredential), pageSize: 1), store).Register(new()).Build();
        async Task<long[]> Pending(HttpClient client) => [.. (await client.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token))!.PendingRevisions];
        static T Success<T>(Outcome<T> value) => Assert.IsType<Outcome<T>.Success>(value).Value;
    }

    private sealed class ReplicaFixtureVerifier(IReadOnlyDictionary<string, AuthenticatedHostInstance> identities) : IHostAccessTokenVerifier
    {
        public ValueTask<AuthenticatedHostInstance?> VerifyAsync(string token, CancellationToken cancellationToken) =>
            ValueTask.FromResult(identities.GetValueOrDefault(token));
    }

    private sealed class ReplicaCatalogTransport(HttpMessageHandler inner, Func<Task>? afterFirstRead, Func<long, Task>? beforeAck,
        bool dropFirstAck) : DelegatingHandler(inner)
    {
        public List<CatalogFeedPage> Pages { get; } = [];
        public List<int> Acknowledgements { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                var acknowledgement = (await request.Content!.ReadFromJsonAsync<CatalogAcknowledgement>(token))!;
                Acknowledgements.Add((int)acknowledgement.ThroughRevision);
                if (beforeAck is not null)
                    await beforeAck(acknowledgement.ThroughRevision);
                if (dropFirstAck && Acknowledgements.Count == 1)
                    throw new HttpRequestException("Simulated interrupted acknowledgement.");
                return await base.SendAsync(request, token);
            }
            var response = await base.SendAsync(request, token);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsByteArrayAsync(token);
                Pages.Add(JsonSerializer.Deserialize<CatalogFeedPage>(payload, JsonSerializerOptions.Web)!);
                if (Pages.Count == 1 && afterFirstRead is not null)
                    await afterFirstRead();
            }
            return response;
        }
    }
}
