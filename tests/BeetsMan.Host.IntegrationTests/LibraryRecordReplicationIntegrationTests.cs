using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
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

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Publishes three record families and verifies matching incremental and replacement desktop projections.</summary>
    /// <remarks>2026-11-25: Bind TEST-RECORDS-002 to HTTPS and both LiteDB replica paths.</remarks>
    /// <remarks>2026-11-26: Reuse the publication fixture for the named FR-RECORDS-001#ac-replicate case.</remarks>
    internal async Task ListeningRecordsAndSharedDefaultsConvergeThroughFeedAndSnapshot()
    {
        const string onlineCredential = "library-record-online";
        const string offlineCredential = "library-record-offline";
        var online = Guid.NewGuid();
        var offline = Guid.NewGuid();
        var track = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var foreignLibrary = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var hostPath = Path.Combine(root, "record-host.db");
        var onlinePath = Path.Combine(root, "record-online.db");
        var offlinePath = Path.Combine(root, "record-offline.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "record-music")).FullName;
        SeedHost(hostPath);
        using (var database = new LiteDatabase(hostPath))
        {
            foreach (var peer in new[] { online, offline })
                database.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                    { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        }
        foreach (var path in new[] { onlinePath, offlinePath })
        {
            using (LiteDbDesktopPlaylistStore.Open(path, allowCreate: true)) { }
            using var database = new LiteDatabase(path);
            database.GetCollection<BsonDocument>("listening_records").Insert(new BsonDocument
                { ["_id"] = foreign, ["LibraryId"] = foreignLibrary, ["Kind"] = "Play", ["SourceRevision"] = 10L });
            database.GetCollection<BsonDocument>("pending_operations").Insert(new BsonDocument
                { ["_id"] = pending, ["State"] = "Conflict", ["CommandType"] = "Favorite", ["Payload"] = "retained local draft" });
        }

        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [onlineCredential] = new(library, online), [offlineCredential] = new(library, offline) });
        await using var host = LibraryHostStartup.Build(builder, new(library, hostPath, music, Path.Combine(root, "record-staging"),
            Path.Combine(root, "record-work"), []), verifier, credentials, credentials);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var originHttp = Client(secret);
        using var onlineHttp = Client(onlineCredential);
        using var offlineHttp = Client(offlineCredential);
        var writer = new HttpLibraryRecordHost(originHttp, address, library, instance, new TokenProvider(library, instance, secret));
        var peerWriter = new HttpLibraryRecordHost(onlineHttp, address, library, online, new TokenProvider(library, online, onlineCredential));
        var favorite = new LibraryRecordIntent.Favorite(track, true, 0);
        var favoriteOperation = Guid.NewGuid();
        var favorited = Success(await writer.PublishAsync(favorite, favoriteOperation, Token));
        Assert.Equal(1, favorited.RecordRevision);
        var setDefault = new LibraryRecordIntent.SharedDefault(SharedLibraryDefaults.AutomaticFadeDuration, TimeSpan.FromSeconds(4).Ticks, 0);
        var defaultOperation = Guid.NewGuid();
        var configured = Success(await writer.PublishAsync(setDefault, defaultOperation, Token));
        Assert.Equal(2, configured.RecordRevision);
        var first = new LibraryRecordIntent.Play(Guid.NewGuid(), track, new DateTimeOffset(2026, 11, 24, 14, 2, 9, TimeSpan.Zero).AddTicks(17));
        var second = first with { PlayId = Guid.NewGuid(), PlayedAtUtc = first.PlayedAtUtc.AddSeconds(1) };
        var firstOperation = Guid.NewGuid();
        var firstPlay = Success(await writer.PublishAsync(first, firstOperation, Token));
        Assert.Equal(3, firstPlay.RecordRevision);
        var secondPlay = Success(await peerWriter.PublishAsync(second, Guid.NewGuid(), Token));
        Assert.Equal(4, secondPlay.RecordRevision);

        using (var store = LiteDbDesktopPlaylistStore.Open(onlinePath, allowCreate: false))
            Assert.Equal(4, (await PullAsync(store, onlineHttp, online, onlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
        using (var database = new LiteDatabase(onlinePath))
        {
            Assert.Equal(track, database.GetCollection<BsonDocument>("listening_records").FindById(favorited.RecordId)["TrackId"].AsGuid);
            var cached = database.GetCollection<BsonDocument>("shared_preference_cache").FindById(configured.RecordId);
            Assert.Equal(TimeSpan.FromSeconds(4).Ticks, cached["DurationTicks"].AsInt64);
        }

        var unmark = new LibraryRecordIntent.Favorite(track, false, favorited.RecordRevision);
        var cleared = Success(await peerWriter.PublishAsync(unmark, Guid.NewGuid(), Token));
        Assert.Equal(5, cleared.RecordRevision);
        var reset = Success(await writer.PublishAsync(setDefault with { DurationTicks = null, ExpectedRevision = configured.RecordRevision }, Guid.NewGuid(), Token));
        Assert.Equal(6, reset.RecordRevision);
        var replayedPlay = Success(await peerWriter.PublishAsync(first, Guid.NewGuid(), Token));
        var replayed = Success(await writer.PublishAsync(favorite, favoriteOperation, Token)) == favorited &&
            Success(await writer.PublishAsync(first, firstOperation, Token)) == firstPlay &&
            Success(await writer.PublishAsync(setDefault, defaultOperation, Token)) == configured &&
            replayedPlay.RecordId == first.PlayId && replayedPlay.RecordRevision == firstPlay.RecordRevision && replayedPlay.FeedRevision == reset.FeedRevision;
        var stale = await writer.PublishAsync(favorite with { ExpectedRevision = favorited.RecordRevision }, Guid.NewGuid(), Token) is
            Outcome<LibraryRecordReceipt>.Conflict { Code: "stale-library-record", ActualRevision: 5 };
        var clashingPlay = await writer.PublishAsync(first with { PlayedAtUtc = first.PlayedAtUtc.AddTicks(1) }, Guid.NewGuid(), Token) is
            Outcome<LibraryRecordReceipt>.Conflict { Code: "play-identity-conflict", ActualRevision: 3 };
        var sharedPath = LibraryRecordRoutes.PublishPath(library);
        using var localOnly = await originHttp.PostAsJsonAsync(sharedPath,
            new PublishLibraryRecordRequest(instance, Guid.NewGuid(), new LibraryRecordIntent.SharedDefault("local.musicPath", 1, 0)), Token);
        using var impersonation = await onlineHttp.PostAsJsonAsync(sharedPath, new PublishLibraryRecordRequest(instance, Guid.NewGuid(), first), Token);
        var delivered = Assert.IsType<CatalogFeedPage>(await originHttp.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 0, null, 20), Token));
        var changes = delivered.Revisions.SelectMany(revision => revision.Changes).ToArray();
        var offlinePending = Assert.IsType<CatalogNotifications>(await offlineHttp.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token));
        Assert.Equal(Enumerable.Range(1, 6).Select(revision => (long)revision), offlinePending.PendingRevisions);

        using (var store = LiteDbDesktopPlaylistStore.Open(onlinePath, allowCreate: false))
        {
            Assert.Equal(6, (await PullAsync(store, onlineHttp, online, onlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
            var wouldInsert = Guid.NewGuid();
            var forged = new CatalogFeedPage(library, online, 6, 7, 7, [new(7, instance, Guid.NewGuid(),
                [new(CatalogEntityKind.Play, CatalogEntityOperation.Upsert, wouldInsert, new ReplicaPlay(track, first.PlayedAtUtc)),
                    new(CatalogEntityKind.Play, CatalogEntityOperation.Delete, foreign)])]);
            Assert.IsType<Outcome<ReplicationState>.Conflict>(await store.ApplyCatalogPageAsync(forged, Token));
            Assert.Equal(6, Success(await store.ReadReplicaAsync(library, online, Token)).Cursor);
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(offlinePath, allowCreate: false))
            Assert.Equal(6, (await PullAsync(store, offlineHttp, offline, offlineCredential, ReplicaRecoveryMode.Snapshot)).Cursor);
        var fromFeed = Inspect(onlinePath);
        var fromSnapshot = Inspect(offlinePath);
        Assert.Contains(fromFeed.Rows, row => row.Id == first.PlayId && row.PlayedAt == first.PlayedAtUtc.UtcTicks && row.Revision == 3);
        Assert.Contains(fromFeed.Rows, row => row.Id == second.PlayId && row.PlayedAt == second.PlayedAtUtc.UtcTicks && row.Revision == 4);
        Assert.Contains(fromFeed.Rows, row => row.Id == favorited.RecordId && row.Tombstone && row.Revision == 5);
        Assert.Contains(fromFeed.Rows, row => row.Id == configured.RecordId && row.Tombstone && row.Revision == 6);
        LibraryRecordReplicationAssertions.Verify(new(delivered.UpperRevision,
            changes.Count(change => change.Kind == CatalogEntityKind.Favorite && change.Operation == CatalogEntityOperation.Delete),
            changes.Count(change => change.Kind == CatalogEntityKind.SharedDefault && change.Operation == CatalogEntityOperation.Delete),
            changes.Where(change => change.Entity is ReplicaPlay).Select(change => change.RecordId).Distinct().Count(), replayed,
            stale, clashingPlay, localOnly.StatusCode == HttpStatusCode.BadRequest, impersonation.StatusCode == HttpStatusCode.Forbidden,
            fromFeed.Rows.Count == 4 && fromFeed.Rows.SequenceEqual(fromSnapshot.Rows), fromFeed.Preserved && fromSnapshot.Preserved));

        // 2026-11-25: Bind the STB07 shared Boolean default and tombstone to HTTPS for FR-SETTINGS-001#ac-availability.
        const string notificationKey = NotificationPreferences.SyncCompleted;
        var notificationId = LibraryRecordIdentity.SharedDefault(library, notificationKey);
        using (var store = LiteDbDesktopPlaylistStore.Open(onlinePath, allowCreate: false))
        {
            var settings = new PlaybackPreferenceHandlers(library, store, peerWriter).Register(new()).Build();
            var edited = Success(await settings.SendAsync(new SetSharedPreferences(NotificationPreferences.Category,
                ImmutableDictionary<string, PreferenceValue>.Empty.Add(notificationKey, new PreferenceValue.Flag(true)), 0),
                new(new(Guid.NewGuid(), online), Guid.NewGuid()), Token));
            Assert.Equal(7, edited.HostRevision);
            Assert.Equal(new PreferenceValue.Flag(true), edited.Preferences.Single(item => item.Key == notificationKey).Value);
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(offlinePath, allowCreate: false))
        {
            Assert.Equal(7, (await PullAsync(store, offlineHttp, offline, offlineCredential, ReplicaRecoveryMode.Snapshot)).Cursor);
            var effective = await ReadPreferenceAsync(store, offline, notificationKey);
            Assert.Equal(new PreferenceValue.Flag(true), effective.Value);
            Assert.Equal(PreferenceSource.HostDefault, effective.Source);
        }
        var update = Success(await writer.PublishAsync(new LibraryRecordIntent.SharedDefault(notificationKey, null, 7, Flag: false),
            Guid.NewGuid(), Token));
        Assert.Equal(notificationId, update.RecordId);
        Assert.Equal(8, update.RecordRevision);
        using (var store = LiteDbDesktopPlaylistStore.Open(onlinePath, allowCreate: false))
        {
            Assert.Equal(8, (await PullAsync(store, onlineHttp, online, onlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
            var effective = await ReadPreferenceAsync(store, online, notificationKey);
            Assert.Equal(new PreferenceValue.Flag(false), effective.Value);
            Assert.Equal(PreferenceSource.HostDefault, effective.Source);
        }
        using var invalidFlag = await originHttp.PostAsJsonAsync(sharedPath, new PublishLibraryRecordRequest(instance, Guid.NewGuid(),
            new LibraryRecordIntent.SharedDefault(NotificationPreferences.PlaybackErrors, null, 0, Flag: false)), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFlag.StatusCode);
        var inherited = Success(await writer.PublishAsync(new LibraryRecordIntent.SharedDefault(notificationKey, null, update.RecordRevision),
            Guid.NewGuid(), Token));
        Assert.Equal(9, inherited.RecordRevision);
        using (var store = LiteDbDesktopPlaylistStore.Open(offlinePath, allowCreate: false))
        {
            Assert.Equal(9, (await PullAsync(store, offlineHttp, offline, offlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
            var effective = await ReadPreferenceAsync(store, offline, notificationKey);
            Assert.Equal(new PreferenceValue.Flag(true), effective.Value);
            Assert.Equal(PreferenceSource.BuiltIn, effective.Source);
        }

        // 2026-11-25: Verify typed Appearance defaults, isolation and inheritance for FR-SETTINGS-001#ac-precedence.
        const string themeKey = AppearancePreferences.Theme;
        using (var store = LiteDbDesktopPlaylistStore.Open(onlinePath, allowCreate: false))
        {
            var settings = new PlaybackPreferenceHandlers(library, store, peerWriter).Register(new()).Build();
            var context = new RequestContext(Guid.NewGuid(), online);
            var shared = Success(await settings.SendAsync(new SetSharedPreferences(AppearancePreferences.Category,
                ImmutableDictionary<string, PreferenceValue>.Empty.Add(themeKey, new PreferenceValue.Text("system")), 0),
                new(context, Guid.NewGuid()), Token));
            Assert.Equal(10, shared.HostRevision);
            var local = Success(await settings.SendAsync(new SetLocalOverride(themeKey, new PreferenceValue.Text("dark"), shared.Revision),
                new(context, Guid.NewGuid()), Token));
            Assert.Equal(PreferenceSource.LocalOverride, local.Source);
            Assert.Equal(new PreferenceValue.Text("system"), local.SharedDefault);
            Assert.Equal(10, (await PullAsync(store, onlineHttp, online, onlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
            Assert.Equal(local.Value, (await ReadPreferenceAsync(store, online, themeKey)).Value);
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(offlinePath, allowCreate: false))
        {
            Assert.Equal(10, (await PullAsync(store, offlineHttp, offline, offlineCredential, ReplicaRecoveryMode.Snapshot)).Cursor);
            var effective = await ReadPreferenceAsync(store, offline, themeKey);
            Assert.Equal(new PreferenceValue.Text("system"), effective.Value);
            Assert.Equal(PreferenceSource.HostDefault, effective.Source);
        }
        using var invalidTheme = await originHttp.PostAsJsonAsync(sharedPath, new PublishLibraryRecordRequest(instance, Guid.NewGuid(),
            new LibraryRecordIntent.SharedDefault(themeKey, null, 10, Flag: true, Text: "dark")), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidTheme.StatusCode);
        var clearedTheme = Success(await writer.PublishAsync(new LibraryRecordIntent.SharedDefault(themeKey, null, 10), Guid.NewGuid(), Token));
        Assert.Equal(11, clearedTheme.RecordRevision);
        using (var store = LiteDbDesktopPlaylistStore.Open(offlinePath, allowCreate: false))
        {
            Assert.Equal(11, (await PullAsync(store, offlineHttp, offline, offlineCredential, ReplicaRecoveryMode.Incremental)).Cursor);
            var effective = await ReadPreferenceAsync(store, offline, themeKey);
            Assert.Equal(new PreferenceValue.Text("system"), effective.Value);
            Assert.Equal(PreferenceSource.BuiltIn, effective.Source);
            Assert.Null(effective.SharedDefault);
        }

        async Task<EffectivePreference> ReadPreferenceAsync(LiteDbDesktopPlaylistStore store, Guid desktop, string key) =>
            Success(await new PlaybackPreferenceHandlers(library, store).Register(new()).Build()
                .QueryAsync(new GetPreference(key), new(Guid.NewGuid(), desktop), Token));

        HttpClient Client(string token)
        {
            var http = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        async Task<ReplicationState> PullAsync(LiteDbDesktopPlaylistStore store, HttpClient client, Guid peer, string token, ReplicaRecoveryMode mode)
        {
            var gateway = new HttpCatalogReplicaHost(client, address, library, peer, new TokenProvider(library, peer, token), pageSize: 2);
            var bus = new CatalogReplicaHandlers(library, peer, gateway, store).Register(new()).Build();
            var cursor = Success(await store.ReadReplicaAsync(library, peer, Token)).Cursor;
            return Success(await bus.SendAsync(new ResumeReplication(library, peer, mode, cursor), new(new(Guid.NewGuid(), peer), Guid.NewGuid()), Token));
        }

        (List<CachedRecord> Rows, bool Preserved) Inspect(string path)
        {
            using var database = new LiteDatabase(path);
            var listening = database.GetCollection<BsonDocument>("listening_records");
            IEnumerable<BsonDocument> rows = listening.Find(Query.EQ("LibraryId", library)).Concat(
                database.GetCollection<BsonDocument>("shared_preference_cache").Find(Query.EQ("LibraryId", library)));
            var draft = database.GetCollection<BsonDocument>("pending_operations").FindById(pending);
            return (rows.Select(row => new CachedRecord(row["_id"].AsGuid, row["SourceRevision"].AsInt64,
                row["Deleted"].IsBoolean && row["Deleted"].AsBoolean, row["PlayedAtUtcTicks"].IsInt64 ? row["PlayedAtUtcTicks"].AsInt64 : null))
                .OrderBy(row => row.Id).ToList(), listening.FindById(foreign)?["LibraryId"].AsGuid == foreignLibrary && draft?["Payload"].AsString == "retained local draft");
        }

        static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    private sealed record CachedRecord(Guid Id, long Revision, bool Tombstone, long? PlayedAt);
}
