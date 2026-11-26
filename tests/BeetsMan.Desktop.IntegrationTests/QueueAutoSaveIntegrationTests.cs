using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises actual queue checkpoints and ordered offline publication through an independent playlist owner.</summary>
/// <remarks>2026-11-24: Bind TEST-PLAYER-001 with LiteDB for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class PLAYERAcceptanceTests
{
    private readonly Guid session = Guid.NewGuid();
    private readonly Guid origin = Guid.NewGuid();
    private string DatabasePath => Path.Combine(folder.FullName, "queue.db");

    /// <summary>Reads the playlist name and auto-save state through the generated commands and a reopened database.</summary>
    /// <remarks>2026-11-25: Bind the STB04 playlist name and switch assertions for FR-PLAYER-001#ac-queue-autosave.</remarks>
    [Fact]
    public async Task QueuePresentationReceivesAndRestoresTheBackingPlaylistName()
    {
        var host = Fixture();
        var expected = new QueueBackingReport(host.Version, "Fixture", true, false);
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var (_, dispatcher) = Open(store, host, initialAutoSave: false);
            var playback = new PlaybackPresentation(session);
            var services = new PresentationServices(dispatcher, new InlineScheduler(), origin);
            using var player = new PlaybackViewModel(services, playback);
            using var queue = new QueueViewModel(services, playback);
            await player.LoadCommand.ExecuteAsync(null);
            QueueAutoSaveAssertions.VerifyBacking(new(null, null, false, false), ReadBacking(queue));
            await queue.LoadPlaylistCommand.ExecuteAsync(host.Version);
            QueueAutoSaveAssertions.VerifyBacking(expected, ReadBacking(queue));
            await queue.SetAutoSaveCommand.ExecuteAsync(true);
            QueueAutoSaveAssertions.VerifyBacking(expected with { AutoSave = true }, ReadBacking(queue));
            Assert.Equal(1, host.Reads);
            Assert.Equal(0, host.Writes);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(DatabasePath, false);
        var (_, resumed) = Open(reopened, host, initialAutoSave: false);
        var restored = new PlaybackPresentation(session);
        var restoredServices = new PresentationServices(resumed, new InlineScheduler(), origin);
        using var restoredPlayer = new PlaybackViewModel(restoredServices, restored);
        using var restoredQueue = new QueueViewModel(restoredServices, restored);
        await restoredPlayer.LoadCommand.ExecuteAsync(null);
        QueueAutoSaveAssertions.VerifyBacking(expected, ReadBacking(restoredQueue));
        Assert.Equal(1, host.Reads);

        static QueueBackingReport ReadBacking(QueueViewModel queue)
        {
            var snapshot = Assert.IsType<QueueSnapshot>(queue.Queue);
            return new(snapshot.BackingPlaylist, snapshot.BackingPlaylistName, queue.SetAutoSaveCommand.CanExecute(true), snapshot.AutoSave);
        }
    }

    /// <summary>Saves an ordered edit to the backing playlist only while auto-save is enabled.</summary>
    /// <remarks>2026-11-26: Bind the named production queue case for FR-PLAYER-001#ac-queue-autosave.</remarks>
    [Fact]
    public async Task QueueAutoSave_MatchesContract()
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

    /// <summary>Verifies that a normal queue replay pauses during batching and a discarded edit stays local after restart.</summary>
    /// <remarks>2026-11-24: Bind the desktop checkpoint and exact batch review to TEST-LIBRARY-004 and FR-PLAYER-001#ac-queue-autosave.</remarks>
    [Fact]
    public async Task BatchModeCommitsQueueCheckpointLocallyAndPausesBackgroundPublication()
    {
        var host = Fixture();
        host.Online = false;
        var scope = new LibraryBatchScope(Guid.NewGuid(), origin);
        var direct = Guid.NewGuid();
        var reordered = Guid.NewGuid();
        var appended = Guid.NewGuid();
        var newTrack = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var append = new EnqueueTracks(session, [newTrack], EnqueueMode.Append);
        PlaybackState initial;
        PlaybackState batched;
        int ordinaryAttempts;
        bool durable;
        bool split;
        Guid[] ordinaryOrder;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var (owner, routes) = WithBatch(store);
            var loaded = await Send(routes, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
            var ids = loaded.Queue.Entries.Select(entry => entry.EntryId).ToImmutableArray();
            ordinaryOrder = [ids[1], ids[0], ids[2]];
            initial = await Send(routes, new EditQueue(session, new QueueEdit.Reorder([.. ordinaryOrder]), loaded.Queue.QueueRevision, 7), direct);
            ordinaryAttempts = host.TotalAttempts;
            Get(await routes.SendAsync(new SetLibraryBatchMode(true, 0), Context(), Token));
            host.Online = true;
            Get(await owner.ReplayQueuePlaylistSavesAsync(session, request, Token));

            await Send(routes, new EditQueue(session, new QueueEdit.Reorder([ids[2], ids[1], ids[0]]), initial.Queue.QueueRevision, 7), reordered);
            batched = await Send(routes, append, appended);
            var stored = Get(await store.ReadCheckpointAsync(session, Token))!;
            var queueReceipt = stored.PlaylistBacking?.StagedSave;
            durable = stored.QueueRevision == batched.Queue.QueueRevision && stored.Queue.SequenceEqual(batched.Queue.Entries) &&
                queueReceipt is not null && queueReceipt.OperationId == appended && queueReceipt == batched.Queue.BatchSave &&
                stored.PlaylistBacking?.LastSave?.OperationId == direct;

            // A conflicting local checkpoint must also roll back the stage and its receipt.
            Get(await store.SaveCheckpointAsync(stored with { CheckpointRevision = stored.CheckpointRevision + 1 }, stored.CheckpointRevision, Token));
            var failed = Guid.NewGuid();
            Assert.Equal("stale-playback-checkpoint", Assert.IsType<Outcome<PlaybackState>.Conflict>(await routes.SendAsync(
                new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.Append), Context(failed), Token)).Code);
            var beforeReopen = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, Token));
            split = beforeReopen.Entries.Any(entry => entry.OperationId == failed);
        }

        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, false))
        {
            var (owner, routes) = WithBatch(store);
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, Token));
            Assert.Equal([reordered, appended], review.Entries.Select(entry => entry.OperationId));
            var finalSave = Assert.IsType<LibraryBatchChange.Playlist>(review.Entries[1].Change);
            Assert.Equal(newTrack, finalSave.Entries[^1].TrackId);
            Assert.Equal(7, finalSave.BasePlaylist?.Revision);
            Assert.Equal(session, finalSave.QueueSource?.SessionId);
            var restored = Get(await routes.QueryAsync(new GetPlaybackState(session), request, Token));
            Assert.Equal(batched.Queue.BatchSave, restored.Queue.BatchSave);
            var replayed = await Send(routes, append, appended);
            Assert.Equal(restored.Queue, replayed.Queue);
            Get(await owner.ReplayQueuePlaylistSavesAsync(session, request, Token));
            var hostAttempts = host.TotalAttempts;
            var hostWrites = host.Writes;
            var pending = Get(await store.ReadPendingQueueSavesAsync(session, Token));
            var ordinaryPendingPreserved = pending.Length == 1 && pending[0].Key.OperationId == direct;

            var cleared = Get(await routes.SendAsync(new RevertLibraryBatch(review.Version), Context(), Token));
            var reverted = Get(await routes.QueryAsync(new GetPlaybackState(session), request, Token));
            Assert.Null(reverted.Queue.BatchSave);
            Assert.Equal(restored.Queue.Entries.AsEnumerable(), reverted.Queue.Entries);
            Assert.Empty(Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, Token)).Entries);
            Get(await routes.SendAsync(new SetLibraryBatchMode(false, cleared.Revision), Context(), Token));
            var discarded = Assert.IsType<Outcome<PlaybackState>.Conflict>(await routes.SendAsync(append, Context(appended), Token));
            Get(await owner.ReplayQueuePlaylistSavesAsync(session, request, Token));
            Assert.Equal(ordinaryOrder, host.Entries.Select(entry => entry.EntryId));
            LibraryBatchQueueAssertions.Verify(new(initial.Queue.SaveStatus.ToString(), batched.Queue.SaveStatus.ToString(), restored.Queue.SaveStatus.ToString(),
                reverted.Queue.SaveStatus.ToString(), ordinaryAttempts, hostAttempts, hostWrites, review.Entries.Length,
                [.. finalSave.Entries.Select(entry => entry.EntryId)], [.. restored.Queue.Entries.Select(entry => entry.EntryId)], durable,
                ordinaryPendingPreserved, discarded.Code, [.. host.OnlineAttempts.Select(attempt => attempt.OperationId)], direct, host.Writes, split));
        }

        CommandContext Context(Guid? id = null) => new(request, id ?? Guid.NewGuid());

        (PlaybackSessionHandlers Owner, IRequestDispatcher Dispatcher) WithBatch(LiteDbDesktopPlaylistStore store)
        {
            var batch = new LibraryBatchHandlers(scope, store, host);
            var state = new PlaybackState(session, 0, new(0, [], true), PlaybackCapabilities.None, TransportStatus.Stopped);
            var owner = new PlaybackSessionHandlers(state, DeckId.A, new SilentDeck(), queuePlaylists: new(host, host, store, origin, batch));
            return (owner, owner.Register(batch.Register(new())).Build());
        }

        static T Get<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private async Task<PlaybackState> Send(IRequestDispatcher dispatcher, ICommand<PlaybackState> command, Guid? operation = null) =>
        Value(await dispatcher.SendAsync(command, new(new(Guid.NewGuid(), origin), operation ?? Guid.NewGuid()), Token));

    /// <summary>Adopts the host's final playlist revision after a batched queue publication and restart.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 and TEST-PLAYER-001 to the persisted queue receipt.</remarks>
    [Fact]
    public async Task CommittedQueueBatchRebindsAndTheNextEditUsesItsPublishedRevision()
    {
        var host = Fixture();
        host.PublishBatches = true;
        var scope = new LibraryBatchScope(Guid.NewGuid(), origin);
        var request = new RequestContext(Guid.NewGuid(), origin);
        var secondId = Guid.NewGuid();
        var add = new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.Append);
        PlaybackState staged;
        PlaybackState waiting;
        PlaybackState finalized;
        bool sealRetained;
        bool committedReplay;
        bool changedReplay;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var routes = WithBatch(store);
            var initial = await Send(routes, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
            var order = initial.Queue.Entries.Select(entry => entry.EntryId).Reverse().ToImmutableArray();
            Get(await routes.SendAsync(new SetLibraryBatchMode(true, 0), Context(), Token));
            await Send(routes, new EditQueue(session, new QueueEdit.Reorder(order), initial.Queue.QueueRevision, 7));
            staged = await Send(routes, add, secondId);
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, Token));
            Assert.Equal(2, review.Entries.Length);
            var approval = new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value), KeepModeEnabled: false);
            host.OmitLastBatchPlaylist = true;
            var incomplete = await routes.SendAsync(approval, Context(), Token);
            sealRetained = incomplete is Outcome<LibraryBatchCommitReceipt>.DependencyFailure { Code: "library-batch-receipt-mismatch" } &&
                Get(await routes.QueryAsync(new GetLibraryBatchMode(), request, Token)).IsCommitting;
            waiting = await Read(routes);
            host.OmitLastBatchPlaylist = false;
            Get(await routes.SendAsync(new ResumeLibraryBatchCommit(), Context(), Token));
            finalized = await Read(routes);
            var replayed = await Send(routes, add, secondId);
            committedReplay = replayed.Queue.SaveReceipt is not null && replayed.Queue.SaveReceipt == finalized.Queue.SaveReceipt &&
                replayed.Queue.Entries.SequenceEqual(staged.Queue.Entries) && host.BatchWrites == 1;
            changedReplay = await routes.SendAsync(add with { OrderedTrackIds = [Guid.NewGuid()] }, Context(secondId), Token) is
                Outcome<PlaybackState>.Conflict { Code: "operation-intent-mismatch" };
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, false))
        {
            var routes = WithBatch(store);
            var restored = await Read(routes);
            var removed = restored.Queue.Entries[^1];
            var direct = await Send(routes, new EditQueue(session, new QueueEdit.Remove([removed.EntryId]), restored.Queue.QueueRevision, 9));
            var attempt = Assert.Single(host.OnlineAttempts);
            var duplicate = staged.Queue.Entries.GroupBy(entry => entry.TrackId).Single(group => group.Count() == 2).Key;
            BatchQueueCommitAssertions.Verify(new(staged.Queue.SaveStatus.ToString(), waiting.Queue.SaveStatus.ToString(), finalized.Queue.SaveStatus.ToString(),
                restored.Queue.SaveStatus.ToString(), finalized.Queue.BackingPlaylist!.Revision, restored.Queue.BackingPlaylist!.Revision,
                finalized.Queue.BatchSave is null && restored.Queue.BatchSave is null,
                finalized.Queue.Entries.SequenceEqual(staged.Queue.Entries) && restored.Queue.Entries.SequenceEqual(staged.Queue.Entries),
                host.Entries.Count(entry => entry.TrackId == duplicate) == 2, committedReplay, changedReplay, sealRetained,
                attempt.Revision, direct.Queue.SaveStatus.ToString(), direct.Queue.BackingPlaylist!.Revision, host.TotalAttempts));
        }

        CommandContext Context(Guid? id = null) => new(request, id ?? Guid.NewGuid());
        async Task<PlaybackState> Read(IRequestDispatcher routes) => Get(await routes.QueryAsync(new GetPlaybackState(session), request, Token));
        static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
        IRequestDispatcher WithBatch(LiteDbDesktopPlaylistStore store)
        {
            var batch = new LibraryBatchHandlers(scope, store, host);
            var state = new PlaybackState(session, 0, new(0, [], true), PlaybackCapabilities.None, TransportStatus.Stopped);
            var owner = new PlaybackSessionHandlers(state, DeckId.A, new SilentDeck(), queuePlaylists: new(host, host, store, origin, batch));
            return owner.Register(batch.Register(new())).Build();
        }
    }

    /// <summary>Preserves a playback-only queue change made after auto-save was staged.</summary>
    /// <remarks>2026-11-24: Cover the later local queue revision for TEST-LIBRARY-004 and FR-PLAYER-001#ac-queue-autosave.</remarks>
    [Fact]
    public async Task BatchPublicationRebindsWithoutMarkingANewerPlaybackOnlyOrderAsSaved()
    {
        var host = Fixture();
        host.PublishBatches = true;
        var scope = new LibraryBatchScope(Guid.NewGuid(), origin);
        var request = new RequestContext(Guid.NewGuid(), origin);
        PlaybackState staged;
        PlaybackState latest;
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, true))
        {
            var batch = new LibraryBatchHandlers(scope, store, host);
            var initial = new PlaybackState(session, 0, new(0, [], true), PlaybackCapabilities.None, TransportStatus.Stopped);
            var owner = new PlaybackSessionHandlers(initial, DeckId.A, new SilentDeck(), queuePlaylists: new(host, host, store, origin, batch));
            var routes = owner.Register(batch.Register(new())).Build();
            await Send(routes, new LoadPlaylistQueue(session, host.Version.PlaylistId, 7));
            Get(await routes.SendAsync(new SetLibraryBatchMode(true, 0), Context(), Token));
            staged = await Send(routes, new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.Append));
            await Send(routes, new SetQueueAutoSave(session, false, staged.Queue.QueueRevision));
            latest = await Send(routes, new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.Append));
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, Token));
            Assert.Single(review.Entries);
            Assert.Equal(QueueSaveStatus.NotRequested, (await Read(routes)).Queue.SaveStatus);
            Get(await routes.SendAsync(new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value)), Context(), Token));
            var after = await Read(routes);
            Assert.Equal(8, after.Queue.BackingPlaylist!.Revision);
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(DatabasePath, false))
        {
            var batch = new LibraryBatchHandlers(scope, store, host);
            var initial = new PlaybackState(session, 0, new(0, [], false), PlaybackCapabilities.None, TransportStatus.Stopped);
            var owner = new PlaybackSessionHandlers(initial, DeckId.A, new SilentDeck(), queuePlaylists: new(host, host, store, origin, batch));
            var restored = await Read(owner.Register(batch.Register(new())).Build());
            Assert.Null(restored.Queue.SaveReceipt);
            BatchQueueCommitAssertions.VerifyNewerQueue(restored.Queue.SaveStatus.ToString(), restored.Queue.BackingPlaylist!.Revision,
                restored.Queue.Entries.SequenceEqual(latest.Queue.Entries), host.Entries.Select(entry => entry.EntryId)
                    .SequenceEqual(staged.Queue.Entries.Select(entry => entry.EntryId)));
        }

        CommandContext Context() => new(request, Guid.NewGuid());
        async Task<PlaybackState> Read(IRequestDispatcher routes) => Get(await routes.QueryAsync(new GetPlaybackState(session), request, Token));
        static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    private static HostFixture Fixture()
    {
        var sameTrack = Guid.NewGuid();
        return new(new(Guid.NewGuid(), 7), [new(Guid.NewGuid(), sameTrack), new(Guid.NewGuid(), sameTrack), new(Guid.NewGuid(), Guid.NewGuid())]);
    }

    private sealed class SilentDeck : IPlaybackSeekTarget
    {
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Queue edits do not invoke the audio transport.");
    }

    /// <summary>Applies the scoped ViewModel result synchronously in the TEST-PLAYER-001 fixture.</summary>
    /// <remarks>2026-11-25: Bind the playlist name without creating a view for FR-PLAYER-001#ac-queue-autosave.</remarks>
    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class HostFixture(PlaylistVersion version, ImmutableArray<PlaylistEntry> entries) : IPublishedPlaylistReader, IPlaylistPublicationHost,
        IApprovedLibraryBatchPublisher
    {
        private readonly Dictionary<OperationKey, PlaylistSavedReceipt> receipts = [];
        private LibraryBatchCommitReceipt? batchReceipt;
        public bool Online { get; set; } = true;
        public bool PublishBatches { get; set; }
        public bool OmitLastBatchPlaylist { get; set; }
        public int BatchWrites { get; private set; }
        public bool LoseNextCommittedReceipt { get; set; }
        public int Writes => receipts.Count;
        /// <summary>2026-11-25: Records playlist reads for TEST-PLAYER-001 reopen verification.</summary>
        public int Reads { get; private set; }
        public int TotalAttempts { get; private set; }
        public PlaylistVersion Version { get; private set; } = version;
        public ImmutableArray<PlaylistEntry> Entries { get; private set; } = entries;
        public List<(Guid OperationId, long Revision)> OnlineAttempts { get; } = [];
        public Func<PendingPlaylistSave, Task>? BeforeAttempt { get; set; }

        public void ConfirmExternalEdit() => Version = Version with { Revision = Version.Revision + 1 };

        public Task<Outcome<PublishedPlaylist>> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<Outcome<PublishedPlaylist>>(new Outcome<PublishedPlaylist>.Success(new(Version, "Fixture", Entries)));
        }

        public async Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken token)
        {
            TotalAttempts++;
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

        /// <summary>Models one authoritative publication and an incomplete first response.</summary>
        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId, CancellationToken token)
        {
            if (!PublishBatches)
                throw new InvalidOperationException("This fixture reverts the batch without approving publication.");
            if (batchReceipt is null)
            {
                var baseline = Version;
                foreach (var entry in approved.Entries)
                {
                    var edit = Assert.IsType<LibraryBatchChange.Playlist>(entry.Change);
                    Assert.Equal(baseline, edit.BasePlaylist);
                    Entries = edit.Entries;
                    Version = Version with { Revision = Version.Revision + 1 };
                }
                BatchWrites++;
                batchReceipt = new(scope.LibraryId, approved.Version, operationId, 43,
                    [.. approved.Entries.Select(entry => new LibraryBatchPublishedPlaylist(entry.OperationId, Version))]);
            }
            Assert.Equal(batchReceipt.OperationId, operationId);
            var response = OmitLastBatchPlaylist ? batchReceipt with { Playlists = batchReceipt.Playlists.RemoveAt(batchReceipt.Playlists.Length - 1) } : batchReceipt;
            return Task.FromResult<Outcome<LibraryBatchCommitReceipt>>(new Outcome<LibraryBatchCommitReceipt>.Success(response));
        }
    }
}
