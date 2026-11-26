using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises listening capture through actual playback handlers and the real local outbox transaction.</summary>
/// <remarks>2026-11-25: Bind TEST-RECORDS-002 to repeated occurrences, offline history and recovery.</remarks>
/// <remarks>2026-11-26: Verify stable offline play identities and checkpoint recovery for FR-RECORDS-001#ac-offline.</remarks>
public sealed class PlaybackHistoryIntegrationTests
{
    [Fact]
    public async Task OnlyConfirmedDeckStartsCreateDurableOccurrencesAndRetriesReuseThem()
    {
        var root = Directory.CreateTempSubdirectory("beets-playback-history-");
        try { await VerifyAsync(Path.Combine(root.FullName, "playback.db"), TestContext.Current.CancellationToken); }
        finally { root.Delete(recursive: true); }
    }

    private static async Task VerifyAsync(string path, CancellationToken token)
    {
        var scope = new LibraryRecordScope(Guid.NewGuid(), Guid.NewGuid());
        var context = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var session = Guid.NewGuid();
        var track = Guid.NewGuid();
        var secondDeckTrack = Guid.NewGuid();
        var unavailable = Guid.NewGuid();
        var clock = new ListeningClock(new DateTimeOffset(2026, 11, 25, 0, 47, 15, TimeSpan.Zero));
        var initial = new PlaybackState(session, 0, new(0, [], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek | PlaybackCapabilities.Mixer,
            TransportStatus.Stopped);
        int first;
        int continued;
        int repeated;
        int cued;
        int secondDeck;
        int rejected;
        bool rollback;
        bool samePlay;
        Guid firstOccurrence;
        Guid continuedOccurrence;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var native = new ListeningBackend(unavailable);
            var checkpoint = new InterruptedHistorySave(store);
            var bus = Connect(store, checkpoint, native);
            var started = await Send(new EnqueueTracks(session, [track, track], EnqueueMode.PlayNow), bus);
            first = (await History(bus)).Length;
            firstOccurrence = (await Checkpoint(store)).ActivePlay!.PlayId;
            await Send(new Transport(session, TransportAction.Pause), bus);
            await Send(new Seek(session, track, TimeSpan.FromSeconds(12)), bus);
            await Send(new Transport(session, TransportAction.Play), bus);
            await Send(new Transport(session, TransportAction.Play), bus);
            continued = (await History(bus)).Length;
            Assert.Equal(firstOccurrence, (await Checkpoint(store)).ActivePlay!.PlayId);
            var next = await Send(new Transport(session, TransportAction.Next), bus);
            Assert.Equal(track, next.ActiveTrackId);
            Assert.NotEqual(started.ActiveQueueEntryId, next.ActiveQueueEntryId);
            Assert.NotEqual(firstOccurrence, (await Checkpoint(store)).ActivePlay!.PlayId);
            repeated = (await History(bus)).Length;
            await Send(new CueDeck(session, DeckId.B, secondDeckTrack), bus);
            cued = (await History(bus)).Length;
            await Send(new Transport(session, TransportAction.Play, DeckId.B), bus);
            await Send(new Transport(session, TransportAction.Pause, DeckId.B), bus);
            await Send(new Transport(session, TransportAction.Play, DeckId.B), bus);
            secondDeck = (await History(bus)).Length;
            Assert.Contains(await History(bus), play => play.TrackId == secondDeckTrack);
            Assert.IsType<Outcome<PlaybackState>.Invalid>(await bus.SendAsync(
                new EnqueueTracks(session, [unavailable], EnqueueMode.PlayNow), Command(), token));
            rejected = (await History(bus)).Length;

            await Send(new Transport(session, TransportAction.Stop), bus);
            Assert.Null((await Checkpoint(store)).ActivePlay);
            var before = await Checkpoint(store);
            checkpoint.Interrupt = true;
            var retry = Command();
            var replay = new Transport(session, TransportAction.Play);
            var beforeCalls = native.Changes;
            Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await bus.SendAsync(replay, retry, token));
            rollback = (await Checkpoint(store)).CheckpointRevision == before.CheckpointRevision && (await History(bus)).Length == 3;
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await bus.SendAsync(replay, retry, token));
            Assert.Equal(4, (await History(bus)).Length);
            clock.Advance(TimeSpan.FromMinutes(1));
            Success(await bus.SendAsync(replay, retry, token));
            Assert.Equal(beforeCalls + 1, native.Changes);
            Assert.Equal(3, checkpoint.Attempts.Count);
            samePlay = checkpoint.Attempts.Skip(1).All(play => play == checkpoint.Attempts[0]);
            var durable = await Checkpoint(store);
            continuedOccurrence = durable.ActivePlay!.PlayId;
            Assert.Equal(checkpoint.Attempts[0].PlayId, continuedOccurrence);
            Assert.Contains(await History(bus), play => play.PlayId == continuedOccurrence && play.PlayedAtUtc == checkpoint.Attempts[0].PlayedAtUtc);

            var stale = durable with { CheckpointRevision = durable.CheckpointRevision + 1, Position = TimeSpan.FromSeconds(17) };
            Assert.IsType<Outcome<PlaybackCheckpoint>.Conflict>(await store.SaveCheckpointWithPlayAsync(stale, durable.CheckpointRevision,
                scope, checkpoint.Attempts[0] with { PlayedAtUtc = clock.GetUtcNow() }, token));
            Assert.Equal(durable.CheckpointRevision, (await Checkpoint(store)).CheckpointRevision);
            Assert.Equal(4, (await History(bus)).Length);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var recovered = Connect(reopened, reopened, new ListeningBackend(unavailable));
        var pausedAt = Success(await recovered.QueryAsync(new GetPlaybackState(session), context, token));
        Assert.Equal(TransportStatus.Stopped, pausedAt.Status);
        var resumed = await Send(new Transport(session, TransportAction.Play), recovered);
        Assert.Equal(pausedAt.ActiveQueueEntryId, resumed.ActiveQueueEntryId);
        Assert.Equal(continuedOccurrence, (await Checkpoint(reopened)).ActivePlay!.PlayId);
        var afterRestart = (await History(recovered)).Length;
        await Send(new Transport(session, TransportAction.Stop), recovered);
        await Send(new Transport(session, TransportAction.Play), recovered);
        Assert.NotEqual(continuedOccurrence, (await Checkpoint(reopened)).ActivePlay!.PlayId);
        var all = await History(recovered);
        Assert.All(all, play => Assert.True(play.PendingSync));
        PlaybackHistoryAssertions.Verify(new(first, continued, repeated, cued, secondDeck, rejected, rollback, samePlay, afterRestart,
            all.Length, all.Select(play => play.PlayId).Distinct().Count()));

        CommandContext Command() => new(context, Guid.NewGuid());
        RequestDispatcher Connect(LiteDbDesktopPlaylistStore store, IPlaybackHistoryCheckpointStore checkpoint, IPlaybackTransportTarget native)
        {
            var listening = new LibraryRecordHandlers(scope, store, new OfflineRecordHost());
            var handlers = new PlaybackSessionHandlers(initial, DeckId.A, native, checkpoint, historyScope: scope, clock: clock);
            return listening.Register(handlers.Register(new())).Build();
        }
        async Task<RecordedPlay[]> History(RequestDispatcher bus) => Success(await bus.QueryAsync(new GetListeningHistory(20), context, token)).ToArray();
        async Task<PlaybackCheckpoint> Checkpoint(LiteDbDesktopPlaylistStore store) => Assert.IsType<PlaybackCheckpoint>(Success(await store.ReadCheckpointAsync(session, token)));
        async Task<PlaybackState> Send(ICommand<PlaybackState> command, RequestDispatcher bus) => Success(await bus.SendAsync(command, Command(), token));
        static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class ListeningClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
        internal void Advance(TimeSpan duration) => utc += duration;
    }

    private sealed class OfflineRecordHost : ILibraryRecordHost
    {
        public Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token) =>
            throw new InvalidOperationException("Local playback must stage listening history without reaching the host.");
    }

    /// <summary>Injects loss before commit and again after a real committed history receipt.</summary>
    private sealed class InterruptedHistorySave(IPlaybackHistoryCheckpointStore inner) : IPlaybackHistoryCheckpointStore
    {
        internal bool Interrupt { get; set; }
        internal List<RecordPlay> Attempts { get; } = [];

        public Task<Outcome<PlaybackCheckpoint?>> ReadCheckpointAsync(Guid sessionId, CancellationToken token) => inner.ReadCheckpointAsync(sessionId, token);
        public Task<Outcome<PlaybackCheckpoint>> SaveCheckpointAsync(PlaybackCheckpoint checkpoint, long expectedRevision, CancellationToken token) =>
            inner.SaveCheckpointAsync(checkpoint, expectedRevision, token);

        public async Task<Outcome<PlaybackCheckpoint>> SaveCheckpointWithPlayAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
            LibraryRecordScope scope, RecordPlay play, CancellationToken token)
        {
            if (!Interrupt)
                return await inner.SaveCheckpointWithPlayAsync(checkpoint, expectedRevision, scope, play, token);
            Attempts.Add(play);
            if (Attempts.Count == 1)
                return Lost();
            var committed = await inner.SaveCheckpointWithPlayAsync(checkpoint, expectedRevision, scope, play, token);
            return Attempts.Count == 2 && committed is Outcome<PlaybackCheckpoint>.Success ? Lost() : committed;

            static Outcome<PlaybackCheckpoint> Lost() => new Outcome<PlaybackCheckpoint>.DependencyFailure("local-history-receipt-unavailable", true);
        }
    }

    /// <summary>Reports deterministic deck effects independently of the playback history handler.</summary>
    private sealed class ListeningBackend(Guid unavailable) : IPlaybackTransportTarget
    {
        private readonly Dictionary<DeckId, DeckState> decks = [];
        internal int Changes { get; private set; }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token)
        {
            Changes++;
            if (trackId == unavailable)
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Invalid([new("track-not-available")]));
            var previous = decks.GetValueOrDefault(deck)?.Position ?? TimeSpan.Zero;
            var position = status == TransportStatus.Stopped ? TimeSpan.Zero : startPosition ?? previous;
            decks[deck] = new(deck, trackId, position, TimeSpan.FromMinutes(2), status, true, 1);
            return Reply(deck);
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) => Reply(deck);

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token)
        {
            decks[deck] = decks[deck] with { Position = position };
            return Reply(deck);
        }

        private Task<Outcome<DeckState>> Reply(DeckId deck) => Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(decks[deck]));
    }
}
