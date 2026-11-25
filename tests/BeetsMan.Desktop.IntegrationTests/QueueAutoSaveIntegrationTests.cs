using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises actual queue checkpoints and ordered offline publication through an independent playlist owner.</summary>
/// <remarks>2026-11-24: Bind TEST-PLAYER-001 with LiteDB for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed class QueueAutoSaveIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-queue-autosave-{Guid.NewGuid():N}");
    private readonly Guid session = Guid.NewGuid();
    private readonly Guid origin = Guid.NewGuid();
    private string DatabasePath => Path.Combine(root, "queue.db");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnabledEditSavesOrderedPlaylistAndDisabledEditOnlyChangesQueue()
    {
        var host = Fixture();
        using var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true);
        var (_, dispatcher) = Open(store, host, initialAutoSave: false);
        var loaded = await Send(dispatcher, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
        var before = loaded.Queue.Entries.Select(entry => entry.EntryId).ToImmutableArray();
        Assert.False(loaded.Queue.AutoSave);
        await Send(dispatcher, new SetQueueAutoSave(session, true, loaded.Queue.QueueRevision));
        host.BeforeAttempt = async operation =>
        {
            var checkpoint = Assert.IsType<Outcome<PlaybackCheckpoint?>.Success>(await store.ReadCheckpointAsync(session, Token)).Value!;
            var durable = Assert.IsType<Outcome<PendingPlaylistSave?>.Success>(await store.ReadQueueSaveAsync(operation.Key, Token)).Value!;
            Assert.Equal(operation.Key, checkpoint.PlaylistBacking!.LastSave);
            Assert.Equal(operation.QueueSource!.QueueRevision, checkpoint.QueueRevision);
            Assert.Equal(durable.Entries.Select(entry => entry.EntryId), checkpoint.Queue.Select(entry => entry.EntryId));
        };
        var published = await Send(dispatcher, new EditQueue(session, new QueueEdit.Reorder([before[2], before[0], before[1]]),
            loaded.Queue.QueueRevision, 7));
        Assert.Equal(session, published.Queue.SaveReceipt?.QueueSessionId);
        await Send(dispatcher, new SetQueueAutoSave(session, false, published.Queue.QueueRevision));
        var local = await Send(dispatcher, new EditQueue(session, new QueueEdit.Remove([before[1]]), published.Queue.QueueRevision, 8));
        QueueAutoSaveAssertions.Verify(before, published.Queue, local.Queue, [.. host.Entries.Select(entry => entry.EntryId)], host.Writes);
    }

    [Fact]
    public async Task OfflineSaveChainUsesDurableIdentitiesAndAuthoritativeRevisionsAfterReopen()
    {
        var host = Fixture();
        host.Online = false;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        EditQueue lastRequest;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var (_, dispatcher) = Open(store, host, initialAutoSave: true);
            var loaded = await Send(dispatcher, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
            var ids = loaded.Queue.Entries.Select(entry => entry.EntryId).ToImmutableArray();
            var ordered = await Send(dispatcher, new EditQueue(session, new QueueEdit.Reorder([ids[2], ids[0], ids[1]]), loaded.Queue.QueueRevision, 7), first);
            Assert.Equal(QueueSaveStatus.Pending, ordered.Queue.SaveStatus);
            lastRequest = new EditQueue(session, new QueueEdit.Remove([ids[1]]), ordered.Queue.QueueRevision, 7);
            var removed = await Send(dispatcher, lastRequest, second);
            Assert.Equal(QueueSaveStatus.Pending, removed.Queue.SaveStatus);
            Assert.Null(removed.Queue.SaveReceipt);
            Assert.Equal(0, host.Writes);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(DatabasePath, false);
        var (owner, resumed) = Open(reopened, host, initialAutoSave: false);
        var restored = Value(await resumed.QueryAsync(new GetPlaybackState(session), new(Guid.NewGuid(), origin), Token));
        Assert.False(restored.Queue.AutoSave);
        Assert.Equal(QueueSaveStatus.Pending, restored.Queue.SaveStatus);
        Assert.Equal(2, restored.Queue.Entries.Length);
        host.Online = true;
        var result = Value(await owner.ReplayQueuePlaylistSavesAsync(session, new(Guid.NewGuid(), origin), Token));
        QueueAutoSaveAssertions.VerifyReplay(first, second, host.OnlineAttempts, result.Queue, host.Entries.Select(entry => entry.EntryId));
        Assert.Equal(2, host.Writes);
        var resent = await Send(resumed, lastRequest, second);
        Assert.Equal(result.Queue, resent.Queue);
        Assert.Equal(2, host.Writes);
    }

    [Fact]
    public async Task RemoteConflictPreservesLocalOrderAndPreventsAnUnbasedSuccessorWrite()
    {
        var host = Fixture();
        host.Online = false;
        using var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true);
        var (owner, dispatcher) = Open(store, host, initialAutoSave: true);
        var loaded = await Send(dispatcher, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
        var ids = loaded.Queue.Entries.Select(entry => entry.EntryId).ToImmutableArray();
        var reordered = await Send(dispatcher, new EditQueue(session, new QueueEdit.Reorder([ids[2], ids[0], ids[1]]),
            loaded.Queue.QueueRevision, 7));
        var second = Guid.NewGuid();
        var local = await Send(dispatcher, new EditQueue(session, new QueueEdit.Remove([ids[1]]), reordered.Queue.QueueRevision, 7), second);
        host.ConfirmExternalEdit();
        host.Online = true;
        var result = Value(await owner.ReplayQueuePlaylistSavesAsync(session, new(Guid.NewGuid(), origin), Token));
        Assert.Equal(QueueSaveStatus.Conflict, result.Queue.SaveStatus);
        Assert.Null(result.Queue.SaveReceipt);
        Assert.Equal(local.Queue.Entries, result.Queue.Entries);
        Assert.Equal(0, host.Writes);
        Assert.Single(host.OnlineAttempts);
        var recovered = Assert.IsType<Outcome<PendingPlaylistSave?>.Success>(await store.ReadQueueSaveAsync(new(origin, second), Token)).Value;
        Assert.Equal("queue-save-predecessor-unresolved", recovered?.ConflictCode);
    }

    [Fact]
    public async Task ReopeningRecoversTheHostReceiptForAnAppendWithoutPublishingTwice()
    {
        var host = Fixture();
        host.LoseNextCommittedReceipt = true;
        var operation = Guid.NewGuid();
        ImmutableArray<Guid> order;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var (_, dispatcher) = Open(store, host, initialAutoSave: true);
            await Send(dispatcher, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
            var added = await Send(dispatcher, new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.Append), operation);
            order = added.Queue.Entries.Select(entry => entry.EntryId).ToImmutableArray();
            Assert.Equal(4, order.Length);
            Assert.Equal(QueueSaveStatus.Pending, added.Queue.SaveStatus);
            Assert.Equal(1, host.Writes);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(DatabasePath, false);
        var (owner, _) = Open(reopened, host, initialAutoSave: true);
        var result = Value(await owner.ReplayQueuePlaylistSavesAsync(session, new(Guid.NewGuid(), origin), Token));
        Assert.Equal(QueueSaveStatus.Saved, result.Queue.SaveStatus);
        Assert.Equal(8, result.Queue.SaveReceipt?.Revision);
        Assert.Equal(order.AsEnumerable(), host.Entries.Select(entry => entry.EntryId));
        Assert.Equal([(operation, 7L), (operation, 7L)], host.OnlineAttempts);
        Assert.Equal(1, host.Writes);
    }

    private (PlaybackSessionHandlers Owner, IRequestDispatcher Dispatcher) Open(LiteDbDesktopPlaylistStore store, HostFixture host, bool initialAutoSave)
    {
        var initial = new PlaybackState(session, 0, new(0, [], initialAutoSave), PlaybackCapabilities.None, TransportStatus.Stopped);
        var owner = new PlaybackSessionHandlers(initial, DeckId.A, new SilentDeck(), queuePlaylists: new(host, host, store, origin));
        return (owner, owner.Register(new RequestDispatcher.Builder()).Build());
    }

    private async Task<PlaybackState> Send(IRequestDispatcher dispatcher, ICommand<PlaybackState> command, Guid? operation = null) =>
        Value(await dispatcher.SendAsync(command, new(new(Guid.NewGuid(), origin), operation ?? Guid.NewGuid()), Token));

    private static PlaybackState Value(Outcome<PlaybackState> outcome) => Assert.IsType<Outcome<PlaybackState>.Success>(outcome).Value;

    private static HostFixture Fixture()
    {
        var sameTrack = Guid.NewGuid();
        return new(new(Guid.NewGuid(), 7), [new(Guid.NewGuid(), sameTrack), new(Guid.NewGuid(), sameTrack), new(Guid.NewGuid(), Guid.NewGuid())]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class SilentDeck : IPlaybackSeekTarget
    {
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Queue edits do not invoke the audio transport.");
    }

    private sealed class HostFixture(PlaylistVersion version, ImmutableArray<PlaylistEntry> entries) : IPublishedPlaylistReader, IPlaylistPublicationHost
    {
        private readonly Dictionary<OperationKey, PlaylistSavedReceipt> receipts = [];
        public bool Online { get; set; } = true;
        public bool LoseNextCommittedReceipt { get; set; }
        public int Writes => receipts.Count;
        public PlaylistVersion Version { get; private set; } = version;
        public ImmutableArray<PlaylistEntry> Entries { get; private set; } = entries;
        public List<(Guid OperationId, long Revision)> OnlineAttempts { get; } = [];
        public Func<PendingPlaylistSave, Task>? BeforeAttempt { get; set; }

        public void ConfirmExternalEdit() => Version = Version with { Revision = Version.Revision + 1 };

        public Task<Outcome<PublishedPlaylist>> ReadAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Outcome<PublishedPlaylist>>(new Outcome<PublishedPlaylist>.Success(new(Version, "Fixture", Entries)));

        public async Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken token)
        {
            if (!Online)
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("host-offline", true);
            Assert.Equal(operation.Key.OriginInstanceId, request.OriginInstanceId);
            Assert.True(operation.QueueSource?.ExpectedRevisionPinned);
            OnlineAttempts.Add((operation.Key.OperationId, operation.BasePlaylist!.Revision));
            if (BeforeAttempt is not null)
                await BeforeAttempt(operation);
            if (receipts.TryGetValue(operation.Key, out var existing))
                return new Outcome<PlaylistSavedReceipt>.Success(existing);
            if (operation.BasePlaylist != Version)
                return new Outcome<PlaylistSavedReceipt>.Conflict("stale-saved-playlist", Version.Revision);
            Version = Version with { Revision = Version.Revision + 1 };
            Entries = operation.Entries;
            var receipt = new PlaylistSavedReceipt(Guid.Empty, Version.PlaylistId, Version.Revision, QueueSessionId: operation.QueueSource!.SessionId);
            receipts.Add(operation.Key, receipt);
            if (LoseNextCommittedReceipt)
            {
                LoseNextCommittedReceipt = false;
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-response-lost", true);
            }
            return new Outcome<PlaylistSavedReceipt>.Success(receipt);
        }
    }
}
