using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using static BeetsMan.Desktop.Tests.PlaybackTransportAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Applies the discriminating transport assertions through the production dispatcher.</summary>
/// <remarks>2026-11-24: Bind TEST-PLAYER-001 for persistent transport and queue policies under FR-PLAYER-001#ac-transport.</remarks>
public sealed class PlaybackTransportTests
{
    private static readonly Guid Session = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private readonly RequestContext context = new(Guid.NewGuid(), null);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CommandsAdvanceDuplicateEntriesAndResumeTheCheckpoint()
    {
        var store = new Checkpoints();
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, store);
        var command = new EnqueueTracks(Session, [FirstTrack, FirstTrack, SecondTrack], EnqueueMode.Append);
        var enqueueContext = new CommandContext(context, Guid.NewGuid());
        var queued = StateOf(await dispatcher.SendAsync(command, enqueueContext, Token));
        var replay = StateOf(await dispatcher.SendAsync(command, enqueueContext, Token));
        Assert.Equal(queued.Queue, replay.Queue);

        var start = await Send(dispatcher, new Transport(Session, TransportAction.Play));
        var next = await Send(dispatcher, new Transport(Session, TransportAction.Next));
        var second = await Send(dispatcher, new Transport(Session, TransportAction.Next));
        var previous = await Send(dispatcher, new Transport(Session, TransportAction.Previous));
        var sought = await Send(dispatcher, new Seek(Session, FirstTrack, TimeSpan.FromSeconds(13)));
        var paused = await Send(dispatcher, new Transport(Session, TransportAction.Pause));
        await Send(dispatcher, new SetQueuePlaybackOptions(Session, new QueuePlaybackEdit.Shuffle(true)));
        var settings = await Send(dispatcher, new SetQueuePlaybackOptions(Session, new QueuePlaybackEdit.Repeat(RepeatMode.All)));

        Assert.Equal(TimeSpan.FromSeconds(13), sought.Position);
        Verify(new(settings.Queue.Entries, start.ActiveQueueEntryId!.Value, next.ActiveQueueEntryId!.Value, next.ActiveTrackId!.Value,
            second.ActiveQueueEntryId!.Value, second.ActiveTrackId!.Value, previous.ActiveQueueEntryId!.Value, paused.Status,
            paused.Position, settings.Queue.Shuffle, settings.Queue.Repeat));
        Assert.Equal(4, deck.Calls.Count(call => call.StartPosition.HasValue));
        Assert.Equal(queued.Queue.QueueRevision, settings.Queue.QueueRevision);

        var reopened = new TransportTarget();
        var resumed = Dispatch(reopened, store);
        var restored = StateOf(await resumed.QueryAsync(new GetPlaybackState(Session), context, Token));
        Assert.Equal(TransportStatus.Stopped, restored.Status);
        Assert.Empty(reopened.Calls);
        Assert.Equal(previous.ActiveQueueEntryId, restored.ActiveQueueEntryId);
        Assert.Equal(TimeSpan.FromSeconds(13), restored.Position);
        var played = await Send(resumed, new Transport(Session, TransportAction.Play));
        Assert.Equal(restored.ActiveQueueEntryId, played.ActiveQueueEntryId);
        Assert.Equal(TimeSpan.FromSeconds(13), Assert.Single(reopened.Calls).StartPosition);
    }

    [Fact]
    public async Task FailedCheckpointCanRetryWithoutSkippingAnotherEntry()
    {
        var store = new Checkpoints();
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, store);
        var play = await Send(dispatcher, new EnqueueTracks(Session, [FirstTrack, SecondTrack, FirstTrack], EnqueueMode.PlayNow));
        store.FailNext = true;
        var next = new Transport(Session, TransportAction.Next);
        var attempt = new CommandContext(context, Guid.NewGuid());
        Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await dispatcher.SendAsync(next, attempt, Token));
        var updated = StateOf(await dispatcher.QueryAsync(new GetPlaybackState(Session), context, Token));
        Assert.Equal(play.Queue.Entries[1].EntryId, updated.ActiveQueueEntryId);
        Assert.Equal(play.Queue.Entries[0].EntryId, store.Value?.ActiveEntryId);

        var recovered = StateOf(await dispatcher.SendAsync(next, attempt, Token));
        Assert.Equal(updated.ActiveQueueEntryId, recovered.ActiveQueueEntryId);
        Assert.Equal(2, deck.Calls.Count);
        Assert.Equal(recovered.ActiveQueueEntryId, store.Value?.ActiveEntryId);
    }

    [Fact]
    public async Task RejectedIncomingTrackKeepsOutgoingTrackAndCheckpoint()
    {
        var store = new Checkpoints();
        var deck = new TransportTarget { Unavailable = SecondTrack };
        var dispatcher = Dispatch(deck, store);
        var before = await Send(dispatcher, new EnqueueTracks(Session, [FirstTrack, SecondTrack], EnqueueMode.PlayNow));
        var checkpoint = store.Value;
        Assert.IsType<Outcome<PlaybackState>.Invalid>(await dispatcher.SendAsync(new Transport(Session, TransportAction.Next),
            new(context, Guid.NewGuid()), Token));
        var after = StateOf(await dispatcher.QueryAsync(new GetPlaybackState(Session), context, Token));
        Assert.Same(before, after);
        Assert.Same(checkpoint, store.Value);
    }

    [Fact]
    public async Task PlaybackQueriesProjectTheLiveAudioClockWithoutWritingTheCheckpoint()
    {
        var store = new Checkpoints();
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, store);
        var before = await Send(dispatcher, new EnqueueTracks(Session, [FirstTrack], EnqueueMode.PlayNow));
        var checkpoint = store.Value!.CheckpointRevision;
        deck.Advance(TimeSpan.FromMilliseconds(125));
        var current = StateOf(await dispatcher.QueryAsync(new GetPlaybackState(Session), context, Token));

        PlaybackPositionAssertions.Verify(before.Position, current.Position, checkpoint, store.Value!.CheckpointRevision);
        Assert.Equal(before.Revision, current.Revision);
        Assert.Equal(before.ActiveQueueEntryId, current.ActiveQueueEntryId);
    }

    private static IRequestDispatcher Dispatch(TransportTarget target, Checkpoints checkpoints)
    {
        var state = new PlaybackState(Session, 0, new(0, [], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek,
            TransportStatus.Stopped);
        return new PlaybackSessionHandlers(state, DeckId.A, target, checkpoints).Register(new RequestDispatcher.Builder()).Build();
    }

    private async Task<PlaybackState> Send(IRequestDispatcher dispatcher, ICommand<PlaybackState> action) =>
        StateOf(await dispatcher.SendAsync(action, new(context, Guid.NewGuid()), Token));

    private static PlaybackState StateOf(Outcome<PlaybackState> outcome) => Assert.IsType<Outcome<PlaybackState>.Success>(outcome).Value;

    private sealed class TransportTarget : IPlaybackTransportTarget
    {
        private DeckState? loaded;
        public Guid? Unavailable { get; init; }
        public List<(Guid Track, TimeSpan? StartPosition, TransportStatus Status)> Calls { get; } = [];

        public Task<Outcome<DeckState>> ChangeAsync(DeckId id, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            Calls.Add((track, position, status));
            if (position.HasValue && track == Unavailable)
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Invalid([new("track-not-available")]));
            if (!position.HasValue)
                Assert.Equal(loaded?.TrackId, track);
            var next = new DeckState(id, track, position ?? loaded!.Position, TimeSpan.FromMinutes(1), status, true, 1);
            loaded = next;
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(next));
        }

        public Task<Outcome<DeckState>> SeekAsync(DeckId id, Guid track, TimeSpan position, CancellationToken token)
        {
            Assert.Equal(track, loaded?.TrackId);
            loaded = loaded! with { Position = position };
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(loaded));
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId id, Guid track, CancellationToken token)
        {
            Assert.NotNull(loaded);
            Assert.Equal(track, loaded.TrackId);
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(loaded));
        }

        public void Advance(TimeSpan elapsed) => loaded = loaded! with { Position = loaded.Position + elapsed };
    }

    private sealed class Checkpoints : IPlaybackCheckpointStore
    {
        public PlaybackCheckpoint? Value { get; private set; }
        public bool FailNext { get; set; }

        public Task<Outcome<PlaybackCheckpoint?>> ReadCheckpointAsync(Guid id, CancellationToken token) =>
            Task.FromResult<Outcome<PlaybackCheckpoint?>>(new Outcome<PlaybackCheckpoint?>.Success(Value));

        public Task<Outcome<PlaybackCheckpoint>> SaveCheckpointAsync(PlaybackCheckpoint checkpoint, long expected, CancellationToken token)
        {
            Assert.Equal(Value?.CheckpointRevision ?? 0, expected);
            Assert.Equal(expected + 1, checkpoint.CheckpointRevision);
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult<Outcome<PlaybackCheckpoint>>(new Outcome<PlaybackCheckpoint>.DependencyFailure("store-unavailable", true));
            }
            Value = checkpoint;
            return Task.FromResult<Outcome<PlaybackCheckpoint>>(new Outcome<PlaybackCheckpoint>.Success(checkpoint));
        }
    }
}
