using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
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
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Exercises persisted playlist recovery across desktop restart against the registered HTTPS host.</summary>
    /// <remarks>2026-11-25: Bind the reconnect mock gate to FR-RECORDS-001#ac-offline, FR-PLAYER-001#ac-queue-autosave and FR-LIBRARY-002#ac-batch-local.</remarks>
    /// <remarks>2026-11-25: Bind FR-PLAYER-001#ac-transport while the recovered queue write awaits HTTPS.</remarks>
    [Fact]
    public async Task RegisteredDesktopReplaysOriginalPlaylistWritesAfterRestartAndBatchMode()
    {
        var database = Path.Combine(root, "reconnect-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "reconnect-music")).FullName;
        var first = new PlaylistEntryData(Guid.NewGuid(), Guid.NewGuid());
        var second = new PlaylistEntryData(Guid.NewGuid(), Guid.NewGuid());
        SeedHost(database);
        using (var setup = new LiteDatabase(database))
        {
            foreach (var track in new[] { first, second })
                setup.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                    { ["_id"] = track.TrackId, ["LibraryId"] = library, ["Revision"] = 1L, ["Title"] = track.TrackId.ToString("N") });
        }
        PlaylistSaveResponse queueSource, staleSource;
        using (var store = LiteDbHostPlaylistStore.Open(database, false))
        {
            var writer = new HostPlaylistWriter(store);
            queueSource = await SeedPlaylist("Queue backing");
            staleSource = await SeedPlaylist("Conflict backing");

            async Task<PlaylistSaveResponse> SeedPlaylist(string name) =>
                Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await writer.SaveAsync(library, new(library, instance),
                    new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, name, [first, second]), Token)).Value;
        }

        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance> { [secret] = new(library, instance) });
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        await using var app = LibraryHostStartup.Build(builder, new(library, database, music,
            Path.Combine(root, "reconnect-private"), Path.Combine(root, "reconnect-work"), []), verifier, credentials, credentials);
        await app.StartAsync(Token);
        var endpoint = new Uri(app.Urls.Single());
        var observer = new ReconnectWriteObserver(library, CreateClientHandler(certificate));
        using var client = new HttpClient(observer);
        using var direct = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = endpoint };
        var defaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "reconnect-desktop"), music);
        var context = new RequestContext(Guid.NewGuid(), instance);
        var namedKey = Guid.NewGuid();
        var queueKey = Guid.NewGuid();
        var staleKey = Guid.NewGuid();
        PlaylistSaveRequest[] expected;

        await using (var desktop = OpenDesktop())
        {
            var active = Value(await desktop.StartAsync(Token));
            var bus = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
            var session = Assert.IsType<Guid>(active.PlaybackSessionId);
            await WaitUntil(async () => Value(await bus.QueryAsync(new GetReplicationStatus(library, instance), context, Token)).Cursor >= staleSource.FeedRevision);
            var queue = Value(await Send(bus, new LoadPlaylistQueue(session, queueSource.PlaylistId, queueSource.PlaylistRevision)));
            await Send(bus, new SetQueueAutoSave(session, true, queue.Queue.QueueRevision));
            var named = Value(await Send(bus, new CreatePlaylistDraft(new PlaylistDraftSource.QueueSnapshot(session, 1,
                [first.TrackId, second.TrackId, first.TrackId]))));
            var stale = Value(await Send(bus, new CreatePlaylistDraft(new PlaylistDraftSource.SavedPlaylist(staleSource.PlaylistId, staleSource.PlaylistRevision))));
            stale = Value(await Send(bus, new EditPlaylistDraft(stale.DraftId,
                new PlaylistDraftEdit.Reorder([second.EntryId, first.EntryId]), stale.Revision)));
            observer.Online = false;
            Assert.IsType<Outcome<PlaylistSavedReceipt>.DependencyFailure>(await Send(bus,
                new SavePlaylist(named.DraftId, "New playlist", named.Revision, null), namedKey));
            Assert.IsType<Outcome<PlaylistSavedReceipt>.DependencyFailure>(await Send(bus,
                new SavePlaylist(stale.DraftId, "Conflict backing", stale.Revision, staleSource.PlaylistRevision), staleKey));
            var pendingQueue = Value(await Send(bus, new EditQueue(session, new QueueEdit.Reorder([second.EntryId, first.EntryId]),
                queue.Queue.QueueRevision, queueSource.PlaylistRevision), queueKey));
            Assert.Equal(QueueSaveStatus.Pending, pendingQueue.Queue.SaveStatus);
            Assert.True(Value(await Send(bus, new SetLibraryBatchMode(true, 0))).IsEnabled);
            expected =
            [
                new(instance, namedKey, new(PlaylistSaveSourceKind.Draft, named.DraftId), null, null, "New playlist",
                    [.. named.Entries.Select(item => new PlaylistEntryData(item.EntryId, item.TrackId))]),
                new(instance, staleKey, new(PlaylistSaveSourceKind.Draft, stale.DraftId), staleSource.PlaylistId,
                    staleSource.PlaylistRevision, "Conflict backing", [second, first]),
                new(instance, queueKey, new(PlaylistSaveSourceKind.Queue, session), queueSource.PlaylistId, queueSource.PlaylistRevision,
                    "Queue backing", [second, first])
            ];
            Value(await desktop.StopAsync(Token));
        }

        using var changed = await PostAsync(direct, library,
            new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), staleSource.PlaylistId, staleSource.PlaylistRevision,
                "Host rename", [first, second]), secret);
        changed.EnsureSuccessStatusCode();
        var hostVersion = Assert.IsType<PlaylistSaveResponse>(await changed.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token));
        var readsBeforeRestart = observer.FeedReads;
        observer.Online = true;

        int whileEnabled, batchEntries;
        PlaybackState recovered;
        PlaylistDraft draft;
        await using (var reopened = OpenDesktop())
        {
            var active = Value(await reopened.StartAsync(Token));
            var bus = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
            var mode = Value(await bus.QueryAsync(new GetLibraryBatchMode(), context, Token));
            Assert.True(mode.IsEnabled);
            await WaitUntil(() => Task.FromResult(observer.FeedReads >= readsBeforeRestart + 2));
            whileEnabled = observer.Requests.Count;
            batchEntries = Value(await bus.QueryAsync(new GetLibraryBatchReview(), context, Token)).Entries.Length;
            Assert.False(Value(await Send(bus, new SetLibraryBatchMode(false, mode.Revision))).IsEnabled);
            var session = Assert.IsType<Guid>(active.PlaybackSessionId);
            try
            {
                var held = await observer.QueueWriteHeld.WaitAsync(TimeSpan.FromSeconds(12), Token);
                Assert.Equal(queueKey, held);
                var started = Value(await Send(bus, new Transport(session, TransportAction.Play)).WaitAsync(TimeSpan.FromSeconds(3), Token));
                PlaylistReconnectAssertions.VerifyTransportDuringSave(started.Status, started.ActiveTrackId, started.Queue.SaveStatus,
                    observer.QueueResponseHeld, second.TrackId);
            }
            finally { observer.ReleaseQueueWrite(); }
            await WaitUntil(async () =>
                Value(await bus.QueryAsync(new GetPlaybackState(session), context, Token)).Queue.SaveStatus == QueueSaveStatus.Saved &&
                Value(await bus.QueryAsync(new GetPlaylistDraft(expected[0].Source.SourceId), context, Token)).BasePlaylist is not null);
            recovered = Value(await bus.QueryAsync(new GetPlaybackState(session), context, Token));
            Assert.Equal(TransportStatus.Playing, recovered.Status);
            Assert.Equal(second.TrackId, recovered.ActiveTrackId);
            draft = Value(await bus.QueryAsync(new GetPlaylistDraft(expected[1].Source.SourceId), context, Token));
            Value(await reopened.StopAsync(Token));
        }

        using var local = LiteDbDesktopPlaylistStore.Open(defaults.DatabasePath, allowCreate: false);
        var namedSave = await ReadSave(namedKey);
        var staleSave = await ReadSave(staleKey);
        PlaylistReconnectAssertions.Verify(new(whileEnabled, batchEntries, [.. observer.Requests], namedSave.Status,
            recovered.Queue.SaveStatus, staleSave.Status, staleSave.ActualHostRevision, [.. draft.Entries], observer.Accepted), expected,
            hostVersion.PlaylistRevision);
        Assert.Equal("stale-saved-playlist", staleSave.ConflictCode);
        Assert.Equal(staleSource.PlaylistRevision, draft.BasePlaylist?.Revision);

        DesktopStartup OpenDesktop() => new(defaults, [], library, instance, _ => new ArrivalPlaybackOutput(),
            host: new(endpoint, client, new TokenProvider(library, instance, secret)));
        Task<Outcome<T>> Send<T>(IRequestDispatcher bus, ICommand<T> command, Guid? operation = null) =>
            bus.SendAsync(command, new(context, operation ?? Guid.NewGuid()), Token);
        async Task<PendingPlaylistSave> ReadSave(Guid operation) =>
            Assert.IsType<PendingPlaylistSave>(Value(await local.ExecuteAsync<PendingPlaylistSave?>(
                repos => new Outcome<PendingPlaylistSave?>.Success(repos.Operations.Find(new(instance, operation))), Token)));
        async Task WaitUntil(Func<Task<bool>> observe)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (await observe())
                    return;
                await Task.Delay(25, Token);
            }
            Assert.Fail("The registered desktop did not complete the expected recovery.");
        }
        static T Value<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    private sealed class ReconnectWriteObserver(Guid library, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private volatile bool online = true;
        private int accepted;
        private int feedReads;
        private readonly TaskCompletionSource<Guid> queueWriteHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseQueue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<PlaylistSaveRequest> Requests { get; } = new();
        internal bool Online { set => online = value; }
        internal int Accepted => Volatile.Read(ref accepted);
        internal int FeedReads => Volatile.Read(ref feedReads);
        internal Task<Guid> QueueWriteHeld => queueWriteHeld.Task;
        internal bool QueueResponseHeld => queueWriteHeld.Task.IsCompletedSuccessfully && !releaseQueue.Task.IsCompleted;
        internal void ReleaseQueueWrite() => releaseQueue.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!online)
                throw new HttpRequestException("This desktop is offline.");
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var write = request.Method == HttpMethod.Post && path == PlaylistPublicationRoute.Path(library).TrimStart('/');
            if (write)
            {
                var body = Assert.IsType<PlaylistSaveRequest>(await request.Content!.ReadFromJsonAsync<PlaylistSaveRequest>(token));
                Requests.Enqueue(body);
                if (body.Source.Kind == PlaylistSaveSourceKind.Queue)
                {
                    queueWriteHeld.TrySetResult(body.OperationId);
                    await releaseQueue.Task.WaitAsync(token);
                }
            }
            var response = await base.SendAsync(request, token);
            if (response.IsSuccessStatusCode && write)
                Interlocked.Increment(ref accepted);
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Get && path.EndsWith("/catalog/changes", StringComparison.Ordinal))
                Interlocked.Increment(ref feedReads);
            return response;
        }
    }
}
