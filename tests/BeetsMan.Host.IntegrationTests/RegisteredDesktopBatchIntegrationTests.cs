using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Tests;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Runs queue and named playlist edits through the composed desktop, authenticated host, and a second desktop.</summary>
    /// <remarks>2026-11-25: Bind FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit and ac-batch-revert with FR-PLAYER-001#ac-queue-autosave.</remarks>
    /// <remarks>2026-11-25: Verify automatic catch-up and lost acknowledgement recovery for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
    [Fact]
    public async Task RegisteredDesktopSharesTheBatchGateForPlaylistsAndQueueAutoSave()
    {
        const string peerToken = "registered-workspace-peer";
        const string offlineToken = "registered-offline-peer";
        var hostDatabase = Path.Combine(root, "registered-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "registered-music")).FullName;
        var peer = Guid.NewGuid();
        var offline = Guid.NewGuid();
        var first = new PlaylistEntry(Guid.NewGuid(), Guid.NewGuid());
        var second = new PlaylistEntry(Guid.NewGuid(), Guid.NewGuid());
        SeedHost(hostDatabase);
        using (var setup = new LiteDatabase(hostDatabase))
        {
            foreach (var recipient in new[] { peer, offline })
                setup.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                    { ["_id"] = recipient, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            foreach (var song in new[] { first, second })
                setup.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                    { ["_id"] = song.TrackId, ["LibraryId"] = library, ["Revision"] = 1L, ["Title"] = song.TrackId.ToString("N") });
        }
        PlaylistSaveResponse source;
        using (var host = LiteDbHostPlaylistStore.Open(hostDatabase, false))
            source = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await new HostPlaylistWriter(host).SaveAsync(library,
                new(library, instance), new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                    "Queue source", [new(first.EntryId, first.TrackId), new(second.EntryId, second.TrackId)]), Token)).Value;

        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [peerToken] = new(library, peer), [offlineToken] = new(library, offline) });
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        await using var hostApp = LibraryHostStartup.Build(builder, new(library, hostDatabase, music,
            Path.Combine(root, "registered-private"), Path.Combine(root, "registered-work"), []), verifier, credentials, credentials);
        await hostApp.StartAsync(Token);
        var endpoint = new Uri(hostApp.Urls.Single());
        var writeObserver = new RegisteredDesktopWriteObserver(library, CreateClientHandler(certificate));
        using var client = new HttpClient(writeObserver);
        var binding = new DesktopHostBinding(endpoint, client, new TokenProvider(library, instance, secret));
        var defaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "origin"), music);
        await using var local = new DesktopStartup(defaults, [], library, instance, host: binding);
        var workspace = Value(await local.StartAsync(Token));
        var dispatcher = workspace.Workspace!;
        var session = Assert.IsType<Guid>(workspace.PlaybackSessionId);
        var origin = new RequestContext(Guid.NewGuid(), instance);
        var synced = await WaitForCursorAsync(dispatcher, instance, source.FeedRevision);
        var peerDefaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "peer"), music);
        var receiveObserver = new AutomaticPeerObserver(library, source.FeedRevision, CreateClientHandler(certificate));
        using var peerClient = new HttpClient(receiveObserver);
        await using var peerDesktop = new DesktopStartup(peerDefaults, [], library, peer,
            host: new(endpoint, peerClient, new TokenProvider(library, peer, peerToken)));
        var peerWorkspace = Value(await peerDesktop.StartAsync(Token));
        var peerBus = Assert.IsAssignableFrom<IRequestDispatcher>(peerWorkspace.Workspace);
        var peerRequest = new RequestContext(Guid.NewGuid(), peer);
        await WaitForCursorAsync(peerBus, peer, source.FeedRevision);
        var loaded = await Submit(new LoadPlaylistQueue(session, source.PlaylistId, source.PlaylistRevision));
        var loadedFromReplica = synced.Cursor > 0 && loaded.Queue.Entries.Select(entry => entry.EntryId).SequenceEqual([first.EntryId, second.EntryId]);
        await Submit(new SetQueueAutoSave(session, true, loaded.Queue.QueueRevision));
        var mode = await Submit(new SetLibraryBatchMode(true, 0));
        var draft = await Submit(new CreatePlaylistDraft(new PlaylistDraftSource.Empty()));
        draft = await Submit(new EditPlaylistDraft(draft.DraftId, new PlaylistDraftEdit.Append([first.TrackId]), draft.Revision));
        var save = new SavePlaylist(draft.DraftId, "Reviewed draft", draft.Revision, null);

        var (staged, queue, review) = await StageBothAsync(loaded.Queue.QueueRevision);
        var stagedLocally = mode.IsEnabled && staged.LocalBatch is not null && queue.Queue.SaveStatus == QueueSaveStatus.StagedInBatch;
        var reviewMatches = ReviewMatches(review, staged, queue);
        var writesBeforeApproval = writeObserver.MutationRequests;
        var reverted = await Submit(new RevertLibraryBatch(review.Version));
        using var directClient = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = endpoint };
        using var readHost = new HttpRequestMessage(HttpMethod.Get, CatalogReplicationRoutes.ChangesPath(library, synced.Cursor, null, 10));
        readHost.Headers.Authorization = new("Bearer", secret);
        using var hostRead = await directClient.SendAsync(readHost, Token);
        hostRead.EnsureSuccessStatusCode();
        var unchanged = await hostRead.Content.ReadFromJsonAsync<CatalogFeedPage>(Token);
        var reloaded = await Submit(new LoadPlaylistQueue(session, source.PlaylistId, source.PlaylistRevision));
        var revertKeptHost = reverted.ChangeCount == 0 && unchanged!.Revisions.IsEmpty &&
            reloaded.Queue.Entries.Select(entry => entry.EntryId).SequenceEqual([first.EntryId, second.EntryId]);

        (staged, queue, review) = await StageBothAsync(reloaded.Queue.QueueRevision);
        reviewMatches &= ReviewMatches(review, staged, queue);
        writesBeforeApproval += writeObserver.MutationRequests;
        var readCountBeforeApproval = receiveObserver.FeedAfter.Count;
        await WaitForAsync(() => receiveObserver.FeedAfter.Count > readCountBeforeApproval);
        var noEarlyDelivery = Value(await peerBus.QueryAsync(new GetReplicationStatus(library, peer), peerRequest, Token)).Cursor == source.FeedRevision;
        var committed = await Submit(new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value)));
        var committedQueue = await dispatcher.QueryAsync(new GetPlaybackState(session), origin, Token);
        var acceptedQueue = Value(committedQueue).Queue;
        var adopted = acceptedQueue.SaveStatus == QueueSaveStatus.Saved && acceptedQueue.BackingPlaylist?.Revision == source.PlaylistRevision + 1;
        var publishedBoth = committed.Playlists.Length == 2 && committed.Playlists.Select(item => item.EntryOperationId)
            .SequenceEqual(review.Entries.Select(entry => entry.OperationId));

        var replica = await WaitForCursorAsync(peerBus, peer, committed.CatalogRevision);
        await WaitForAsync(() => receiveObserver.AckAttempts.Count(cursor => cursor == committed.CatalogRevision) >= 2);
        var copy = Value(await peerBus.SendAsync(new CreatePlaylistDraft(new PlaylistDraftSource.SavedPlaylist(
            source.PlaylistId, source.PlaylistRevision + 1)), new(peerRequest, Guid.NewGuid()), Token));
        var peerOrder = replica.Cursor == committed.CatalogRevision && copy.Entries.SequenceEqual([second, first]);
        using var offlineClient = new HttpClient(CreateClientHandler(certificate));
        var offlineRetained = (await ReadNotifications(offlineClient, offline, offlineToken)).PendingRevisions.Contains(committed.CatalogRevision);
        var onlineCleared = (await ReadNotifications(peerClient, peer, peerToken)).PendingRevisions.IsEmpty;
        var offlineDefaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "offline"), music);
        await using var recoveredDesktop = new DesktopStartup(offlineDefaults, [], library, offline,
            host: new(endpoint, offlineClient, new TokenProvider(library, offline, offlineToken)));
        var recovered = Value(await recoveredDesktop.StartAsync(Token));
        var caughtUp = await WaitForCursorAsync(recovered.Workspace!, offline, committed.CatalogRevision);
        var notifications = await ReadNotifications(offlineClient, offline, offlineToken);
        for (var attempt = 0; attempt < 100 && !notifications.PendingRevisions.IsEmpty; attempt++)
        {
            await Task.Delay(25, Token);
            notifications = await ReadNotifications(offlineClient, offline, offlineToken);
        }
        RegisteredDesktopBatchAssertions.Verify(new(loadedFromReplica, stagedLocally, reviewMatches, writesBeforeApproval,
            revertKeptHost, writeObserver.MutationRequests, publishedBoth, adopted, peerOrder));
        RegisteredDesktopBatchAssertions.VerifyAutomaticDelivery(new(synced.Cursor == source.FeedRevision, noEarlyDelivery,
            peerOrder, onlineCleared && receiveObserver.Dropped == 1 && receiveObserver.AckAttempts.Count(revision => revision == committed.CatalogRevision) == 2,
            receiveObserver.FeedAfter.Skip(receiveObserver.ReadsWhenDropped).All(after => after == committed.CatalogRevision),
            offlineRetained, caughtUp.Cursor == committed.CatalogRevision && notifications.PendingRevisions.IsEmpty));

        async Task<ReplicationState> WaitForCursorAsync(IRequestDispatcher bus, Guid recipient, long revision)
        {
            var context = new RequestContext(Guid.NewGuid(), recipient);
            var query = new GetReplicationStatus(library, recipient);
            var current = Value(await bus.QueryAsync(query, context, Token));
            for (var attempt = 0; attempt < 500 && current.Cursor < revision; attempt++)
            {
                await Task.Delay(25, Token);
                current = Value(await bus.QueryAsync(query, context, Token));
            }
            Assert.Equal(revision, current.Cursor);
            return current;
        }

        async Task WaitForAsync(Func<bool> observed)
        {
            for (var attempt = 0; attempt < 500 && !observed(); attempt++)
                await Task.Delay(25, Token);
            Assert.True(observed());
        }

        async Task<CatalogNotifications> ReadNotifications(HttpClient transport, Guid recipient, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, CatalogReplicationRoutes.NotificationsPath(library)));
            request.Headers.Authorization = new("Bearer", token);
            using var response = await transport.SendAsync(request, Token);
            response.EnsureSuccessStatusCode();
            var receipt = Assert.IsType<CatalogNotifications>(await response.Content.ReadFromJsonAsync<CatalogNotifications>(Token));
            Assert.Equal(recipient, receipt.RecipientInstanceId);
            return receipt;
        }

        async Task<(PlaylistSavedReceipt Draft, PlaybackState Queue, LibraryBatchReview Review)> StageBothAsync(long revision)
        {
            var saved = await Submit(save);
            var edited = await Submit(new EditQueue(session, new QueueEdit.Reorder([second.EntryId, first.EntryId]), revision, source.PlaylistRevision));
            return (saved, edited, Value(await dispatcher.QueryAsync(new GetLibraryBatchReview(), origin, Token)));
        }
        bool ReviewMatches(LibraryBatchReview listed, PlaylistSavedReceipt saved, PlaybackState stagedQueue) =>
            listed is { ReviewId: not null, Entries.Length: 2 } && listed.Entries[0].OperationId == saved.LocalBatch?.OperationId &&
            listed.Entries[0].Change is LibraryBatchChange.Playlist { Name: "Reviewed draft" } &&
            listed.Entries[1].OperationId == stagedQueue.Queue.BatchSave?.OperationId &&
            listed.Entries[1].Change is LibraryBatchChange.Playlist { QueueSource.SessionId: var queueSession } ordered &&
            queueSession == session && ordered.Entries.SequenceEqual([second, first]);
        async Task<T> Submit<T>(ICommand<T> command) => Value(await dispatcher.SendAsync(command, new(origin, Guid.NewGuid()), Token));
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class RegisteredDesktopWriteObserver(Guid library, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal int MutationRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Post && (path == ApprovedLibraryBatchRoutes.ApprovePath(library).TrimStart('/') ||
                path == PlaylistPublicationRoute.Path(library).TrimStart('/')))
                MutationRequests++;
            return base.SendAsync(request, token);
        }
    }

    private sealed class AutomaticPeerObserver(Guid library, long baseline, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int dropped;
        private int readsWhenDropped;
        internal ConcurrentQueue<long> FeedAfter { get; } = new();
        internal ConcurrentQueue<long> AckAttempts { get; } = new();
        internal int Dropped => Volatile.Read(ref dropped);
        internal int ReadsWhenDropped => Volatile.Read(ref readsWhenDropped);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/catalog/changes", StringComparison.Ordinal) &&
                QueryHelpers.ParseQuery(uri.Query).TryGetValue("after", out var value) &&
                long.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var after))
                FeedAfter.Enqueue(after);
            var receipt = await base.SendAsync(request, token);
            if (request.Method != HttpMethod.Post || uri.AbsolutePath.TrimStart('/') != CatalogReplicationRoutes.AcknowledgePath(library) ||
                request.Content is null)
                return receipt;
            var acknowledged = await request.Content.ReadFromJsonAsync<CatalogAcknowledgement>(token);
            if (acknowledged is null)
                return receipt;
            AckAttempts.Enqueue(acknowledged.ThroughRevision);
            if (acknowledged.ThroughRevision <= baseline || !receipt.IsSuccessStatusCode || Interlocked.CompareExchange(ref dropped, 1, 0) != 0)
                return receipt;
            Volatile.Write(ref readsWhenDropped, FeedAfter.Count);
            receipt.Dispose();
            throw new HttpRequestException("The peer did not receive the committed acknowledgement.");
        }
    }
}
