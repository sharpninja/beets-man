using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using static BeetsMan.Desktop.Tests.PlaybackTransportAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Applies the discriminating transport assertions through the production dispatcher.</summary>
/// <remarks>2026-11-24: Bind TEST-PLAYER-001 for persistent transport and queue policies under FR-PLAYER-001#ac-transport.</remarks>
public sealed partial class PLAYERAcceptanceTests
{
    private static readonly Guid Session = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private readonly RequestContext context = new(Guid.NewGuid(), null);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Checks persistent queue occurrences and each transport and master volume control.</summary>
    /// <remarks>2026-11-26: Bind the named TEST-PLAYER-001 case for FR-PLAYER-001#ac-transport.</remarks>
    [Fact]
    public async Task Transport_MatchesContract()
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
        var level = await Send(dispatcher, new SetMix(Session, new MixEdit.MasterGain(0.35)));
        Assert.Equal(0.35, level.Output!.Gain);
        Assert.Equal(0.35, deck.AppliedMix?.MasterGain);
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

    /// <summary>Cues and plays two independent decks, then applies gain, crossfader and bypass through the output port.</summary>
    /// <remarks>2026-11-26: Bind the named TEST-PLAYER-001 mix case for FR-PLAYER-001#ac-mix.</remarks>
    [Fact]
    public async Task Mix_MatchesContract()
    {
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, new Checkpoints(), PlaybackCapabilities.Mixer | PlaybackCapabilities.Equalizer);
        await Send(dispatcher, new CueDeck(Session, DeckId.A, FirstTrack));
        await Send(dispatcher, new CueDeck(Session, DeckId.B, SecondTrack));
        await Send(dispatcher, new Transport(Session, TransportAction.Play, DeckId.A));
        var playing = await Send(dispatcher, new Transport(Session, TransportAction.Play, DeckId.B));
        Assert.Equal(FirstTrack, playing.Decks.Single(item => item.DeckId == DeckId.A).TrackId);
        Assert.Equal(SecondTrack, playing.Decks.Single(item => item.DeckId == DeckId.B).TrackId);
        Assert.All(playing.Decks, item => Assert.Equal(TransportStatus.Playing, item.Status));

        await Send(dispatcher, new SetMix(Session, new MixEdit.DeckGain(DeckId.A, 0.4)));
        await Send(dispatcher, new SetMix(Session, new MixEdit.DeckGain(DeckId.B, 0.8)));
        var blended = await Send(dispatcher, new SetMix(Session, new MixEdit.CrossfaderPosition(0.5)));
        Assert.Equal(0.4, blended.Decks.Single(item => item.DeckId == DeckId.A).Gain);
        Assert.Equal(0.8, blended.Decks.Single(item => item.DeckId == DeckId.B).Gain);
        Assert.Equal(0.5, blended.Output!.CrossfaderPosition);
        Assert.True(deck.AbandonedFade);

        var bands = PlaybackMixSettings.Default.EqualizerBands.SetItem(3, 6);
        await Send(dispatcher, new SetEqualizer(Session, new EqualizerEdit.ApplyBands(bands)));
        var bypassed = await Send(dispatcher, new SetEqualizer(Session, new EqualizerEdit.Bypass(true)));
        Assert.True(bypassed.Output!.EqualizerBypassed);
        Assert.Equal(bands, bypassed.Output.EqualizerBands);
        Assert.True(deck.AppliedMix!.EqualizerBypassed);
        Assert.Equal(bands, deck.AppliedMix.EqualizerBands);
        var active = deck.AppliedMix;
        var rejected = await dispatcher.SendAsync(new SetMix(Session, new MixEdit.CrossfaderPosition(double.NaN)),
            new(context, Guid.NewGuid()), Token);
        Assert.Equal("invalid-playback-mix", Assert.Single(Assert.IsType<Outcome<PlaybackState>.Invalid>(rejected).Issues).Code);
        Assert.Same(active, deck.AppliedMix);
    }

    /// <summary>Restores the exact duplicate occurrence and starts its audio only with the local toggle enabled.</summary>
    /// <remarks>2026-11-26: Bind the named TEST-PLAYER-001 restart case for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartResume_MatchesContract(bool enabled)
    {
        var first = new QueueEntry(Guid.NewGuid(), FirstTrack);
        var active = new QueueEntry(Guid.NewGuid(), FirstTrack);
        var position = TimeSpan.FromSeconds(37);
        var checkpoint = new PlaybackCheckpoint(Session, 1, 4, [first, active], active.EntryId, position, false, RepeatMode.All,
            new(new(Guid.NewGuid(), 11), "Recent list"), WasPlaying: true);
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, new Checkpoints(checkpoint), restart: new StartupPreference(enabled));
        var projected = StateOf(await dispatcher.QueryAsync(new GetPlaybackState(Session), context, Token));
        Assert.Equal(TransportStatus.Stopped, projected.Status);
        Assert.Empty(deck.Calls);

        var started = await Send(dispatcher, new InitializePlayback(Session));
        await Send(dispatcher, new InitializePlayback(Session));
        Assert.Equal(active.EntryId, started.ActiveQueueEntryId);
        Assert.Equal(position, started.Position);
        Assert.Equal(checkpoint.Queue.AsEnumerable(), started.Queue.Entries.AsEnumerable());
        Assert.Equal(enabled ? TransportStatus.Playing : TransportStatus.Stopped, started.Status);
        if (enabled)
            Assert.Equal((FirstTrack, (TimeSpan?)position, TransportStatus.Playing), Assert.Single(deck.Calls));
        else
            Assert.Empty(deck.Calls);
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

    /// <summary>Checks that a retry cannot advance a queue that has moved since the original checkpoint failure.</summary>
    /// <remarks>2026-11-25: Bind TEST-PLAYER-001 retry assertions to the playback lane for FR-PLAYER-001#ac-transport.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointRetryKeepsTheLatestOccurrenceAfterAnotherFailedAdvance(bool retryLaterOperationFirst)
    {
        var store = new Checkpoints();
        var deck = new TransportTarget();
        var dispatcher = Dispatch(deck, store);
        var initial = await Send(dispatcher, new EnqueueTracks(Session,
            [FirstTrack, SecondTrack, FirstTrack, SecondTrack], EnqueueMode.PlayNow));
        var next = new Transport(Session, TransportAction.Next);
        var first = new CommandContext(context, Guid.NewGuid());
        var second = new CommandContext(context, Guid.NewGuid());

        store.FailNext = true;
        Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await dispatcher.SendAsync(next, first, Token));
        Assert.Equal(initial.Queue.Entries[0].EntryId, store.Value?.ActiveEntryId);

        store.FailAfterNext = true;
        Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await dispatcher.SendAsync(next, second, Token));
        Assert.Equal(initial.Queue.Entries[1].EntryId, store.Value?.ActiveEntryId);
        var latestEntry = initial.Queue.Entries[2].EntryId;
        var latest = StateOf(await dispatcher.QueryAsync(new GetPlaybackState(Session), context, Token));
        Assert.Equal(latestEntry, latest.ActiveQueueEntryId);

        var replayed = StateOf(await dispatcher.SendAsync(next, retryLaterOperationFirst ? second : first, Token));
        var replayedOther = StateOf(await dispatcher.SendAsync(next, retryLaterOperationFirst ? first : second, Token));
        var changedIntent = await dispatcher.SendAsync(new Transport(Session, TransportAction.Previous), first, Token);
        VerifyReplay(latestEntry, new(replayed.ActiveQueueEntryId, replayedOther.ActiveQueueEntryId, store.Value?.ActiveEntryId,
            deck.Calls.Count, (changedIntent as Outcome<PlaybackState>.Conflict)?.Code));
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

    private static IRequestDispatcher Dispatch(TransportTarget target, Checkpoints checkpoints, PlaybackCapabilities additional = PlaybackCapabilities.None,
        IPlaybackRestartPreference? restart = null)
    {
        var state = new PlaybackState(Session, 0, new(0, [], false),
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek | PlaybackCapabilities.Volume | additional,
            TransportStatus.Stopped);
        return new PlaybackSessionHandlers(state, DeckId.A, target, checkpoints, restartPreference: restart).Register(new RequestDispatcher.Builder()).Build();
    }

    private async Task<PlaybackState> Send(IRequestDispatcher dispatcher, ICommand<PlaybackState> action) =>
        StateOf(await dispatcher.SendAsync(action, new(context, Guid.NewGuid()), Token));

    private static PlaybackState StateOf(Outcome<PlaybackState> outcome) => Assert.IsType<Outcome<PlaybackState>.Success>(outcome).Value;

    private sealed class TransportTarget : IPlaybackTransportTarget, IPlaybackMixTarget
    {
        private readonly Dictionary<DeckId, DeckState> loaded = [];
        public Guid? Unavailable { get; init; }
        public List<(Guid Track, TimeSpan? StartPosition, TransportStatus Status)> Calls { get; } = [];
        public PlaybackMixSettings? AppliedMix { get; private set; }
        public bool AbandonedFade { get; private set; }

        /// <summary>Records the requested backend volume for FR-PLAYER-001#ac-transport.</summary>
        public Task<Outcome<bool>> ApplyMixAsync(PlaybackMixSettings settings, bool abandonAutomaticFade, CancellationToken cancellationToken)
        {
            AppliedMix = settings;
            AbandonedFade = abandonAutomaticFade;
            return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true));
        }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId id, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            Calls.Add((track, position, status));
            if (position.HasValue && track == Unavailable)
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Invalid([new("track-not-available")]));
            if (!position.HasValue)
                Assert.Equal(loaded[id].TrackId, track);
            var next = new DeckState(id, track, position ?? loaded[id].Position, TimeSpan.FromMinutes(1), status, true, 1);
            loaded[id] = next;
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(next));
        }

        public Task<Outcome<DeckState>> SeekAsync(DeckId id, Guid track, TimeSpan position, CancellationToken token)
        {
            Assert.Equal(track, loaded[id].TrackId);
            loaded[id] = loaded[id] with { Position = position };
            return ReadAsync(id, track, token);
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId id, Guid track, CancellationToken token)
        {
            Assert.Equal(track, loaded[id].TrackId);
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(loaded[id]));
        }

        public void Advance(TimeSpan elapsed) => loaded[DeckId.A] = loaded[DeckId.A] with { Position = loaded[DeckId.A].Position + elapsed };
    }

    /// <summary>Returns the independently selected local restart toggle for FR-PLAYER-001#ac-resume-playback-after-restart.</summary>
    private sealed class StartupPreference(bool enabled) : IPlaybackRestartPreference
    {
        public Task<Outcome<bool>> ReadRestartPreferenceAsync(CancellationToken token) =>
            Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(enabled));
    }

    private sealed class Checkpoints(PlaybackCheckpoint? saved = null) : IPlaybackCheckpointStore
    {
        public PlaybackCheckpoint? Value { get; private set; } = saved;
        public bool FailNext { get; set; }
        /// <summary>2026-11-25: Allows repairing an earlier write while failing the next native effect for TEST-PLAYER-001.</summary>
        public bool FailAfterNext { get; set; }

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
            FailNext = FailAfterNext;
            FailAfterNext = false;
            return Task.FromResult<Outcome<PlaybackCheckpoint>>(new Outcome<PlaybackCheckpoint>.Success(checkpoint));
        }
    }
}
