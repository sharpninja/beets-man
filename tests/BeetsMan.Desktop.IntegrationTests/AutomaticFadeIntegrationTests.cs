using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Drives the production fade coordinator from a deterministic deck clock and real playback preferences.</summary>
/// <remarks>2026-11-25: Bind timing, pause, invalidation and failure behavior for FR-PLAYER-001#ac-fade.</remarks>
public sealed partial class PLAYERAcceptanceTests : IDisposable
{
    private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("beets-player-acceptance-");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Follows the audio clock and verifies manual takeover and incoming-track failure handling.</summary>
    /// <remarks>2026-11-26: Bind the named production fade case for FR-PLAYER-001#ac-fade.</remarks>
    [Fact]
    public async Task Fade_MatchesContract()
    {
        using var natural = await OpenAsync();
        var first = natural.Started.Queue.Entries[0];
        var next = natural.Started.Queue.Entries[1];
        natural.Audio.SetPosition(DeckId.A, 3);
        var early = await natural.Tick();
        var didNotStartEarly = early.Output?.FadeActive == false && !natural.Audio.HasDeck(DeckId.B);
        natural.Audio.SetPosition(DeckId.A, 4);
        var started = await natural.Tick();
        var prepared = started.Output?.FadeActive == true && started.ActiveQueueEntryId == first.EntryId &&
            natural.Audio.Events.TakeLast(2).SequenceEqual(["B:Stopped", "B:Playing"]);
        natural.Audio.SetPosition(DeckId.A, 7);
        natural.Audio.SetPosition(DeckId.B, 3);
        var midpoint = await natural.Tick();
        var paused = await natural.Send(new Transport(natural.Session, TransportAction.Pause));
        var frozen = await natural.Tick();
        var pausedBoth = paused.Decks.Count(deck => deck.Status == TransportStatus.Paused) == 2 &&
            frozen.Output?.CrossfaderPosition == midpoint.Output?.CrossfaderPosition && frozen.Decks.All(deck => deck.Status == TransportStatus.Paused);
        await natural.Send(new Transport(natural.Session, TransportAction.Play));
        natural.Audio.SetPosition(DeckId.A, 10);
        natural.Audio.SetPosition(DeckId.B, 6);
        var promoted = await natural.Tick();
        var checkpoint = Value(await natural.Store.ReadCheckpointAsync(natural.Session, Token));
        var promotedExact = promoted.ActiveQueueEntryId == next.EntryId && promoted.ActiveTrackId == next.TrackId &&
            promoted.Position == TimeSpan.FromSeconds(6) && promoted.Output is { FadeActive: false, CrossfaderPosition: 1 } &&
            promoted.Decks.Single(deck => deck.DeckId == DeckId.A).Status == TransportStatus.Stopped && checkpoint?.ActiveEntryId == next.EntryId;

        using var seeking = await OpenAsync();
        await seeking.StartFade();
        var seeked = await seeking.Send(new Seek(seeking.Session, seeking.Started.ActiveTrackId!.Value, TimeSpan.FromSeconds(8)));
        var seekCancelled = seeked is { Position.TotalSeconds: 8, Output.FadeActive: false, Output.CrossfaderPosition: 0 } &&
            seeking.Audio.Status(DeckId.B) == TransportStatus.Stopped;
        await seeking.Tick();
        seeking.Audio.SetPosition(DeckId.A, 9);
        var clamped = (await seeking.Tick()).Output is { FadeActive: true, CrossfaderPosition: 0.5 };

        using var skipping = await OpenAsync();
        await skipping.StartFade();
        var skipped = await skipping.Send(new Transport(skipping.Session, TransportAction.Next));
        var skipCancelled = skipped.ActiveQueueEntryId == skipping.Started.Queue.Entries[1].EntryId && skipped.Position == TimeSpan.Zero &&
            skipped.Output is { FadeActive: false, CrossfaderPosition: 0 } && skipping.Audio.Status(DeckId.B) == TransportStatus.Stopped;

        using var manual = await OpenAsync();
        await manual.StartFade();
        var controlled = await manual.Send(new SetMix(manual.Session, new MixEdit.CrossfaderPosition(0.8)));
        manual.Audio.SetPosition(DeckId.A, 8);
        var later = await manual.Tick();
        var tookOver = controlled.Output is { FadeActive: false, CrossfaderPosition: 0.8 } &&
            later.Output?.CrossfaderPosition == 0.8 && manual.Audio.Status(DeckId.B) == TransportStatus.Playing;

        using var unavailable = await OpenAsync();
        unavailable.Audio.Missing = unavailable.Started.Queue.Entries[1].TrackId;
        var rejected = await unavailable.StartFade();
        await unavailable.Tick();
        var preserved = rejected is { Status: TransportStatus.Playing, Output.CrossfaderPosition: 0, Output.FadeActive: false,
            Output.FadeErrorCode: "playback-fade-incoming-unavailable" } && rejected.ActiveQueueEntryId == unavailable.Started.ActiveQueueEntryId &&
            unavailable.Audio.AttemptsForMissing == 1 && !unavailable.Audio.HasDeck(DeckId.B);
        AutomaticFadeAssertions.Verify(new(didNotStartEarly, prepared, midpoint.Output!.CrossfaderPosition, pausedBoth, promotedExact,
            seekCancelled, skipCancelled, tookOver, preserved, clamped));
    }

    /// <summary>Checks automatic scheduling and projection through the desktop startup and shutdown path.</summary>
    /// <remarks>2026-11-25: Verify that FR-PLAYER-001#ac-fade advances without any client timer or screen.</remarks>
    [Fact]
    public async Task DesktopPumpAdvancesAndPublishesTheFadeUntilOrderlyShutdown()
    {
        var audio = new AudioClock();
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var defaults = JsonBootstrapFile.ResolveDefaults(folder.FullName, Path.Combine(folder.FullName, "music"));
        await using var desktop = new DesktopStartup(defaults, [], library, origin, _ => audio, presentationScheduler: new TestScheduler());
        var started = Value(await desktop.StartAsync(Token));
        var player = Assert.IsType<DesktopPresentation>(started.Presentation).Player;
        var session = started.PlaybackSessionId!.Value;
        var context = new RequestContext(Guid.NewGuid(), origin);
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(started.Workspace);
        Value(await Send(new SetLocalOverride(PlaybackPreferences.AutomaticFadeDuration,
            new PreferenceValue.Duration(TimeSpan.FromSeconds(6)), 0)));
        await player.ToggleAutoFadeCommand.ExecuteAsync(true);
        Assert.Null(player.Failure);
        var queued = Value(await Send(new EnqueueTracks(session, [Guid.NewGuid(), Guid.NewGuid()], EnqueueMode.PlayNow)));
        audio.SetPosition(DeckId.A, 4);
        await WaitFor(() => player.State?.Output?.FadeActive == true);
        audio.SetPosition(DeckId.A, 7);
        audio.SetPosition(DeckId.B, 3);
        await WaitFor(() => player.State?.Output?.CrossfaderPosition == 0.5);
        audio.SetPosition(DeckId.A, 10);
        audio.SetPosition(DeckId.B, 6);
        await WaitFor(() => player.State?.ActiveQueueEntryId == queued.Queue.Entries[1].EntryId);
        Assert.Equal(TimeSpan.FromSeconds(6), player.State!.Position);
        var final = Value(await desktop.StopAsync(Token));
        Assert.Equal(queued.Queue.Entries[1].EntryId, final?.ActiveQueueEntryId);
        Assert.Equal(TimeSpan.FromSeconds(6), final?.Position);
        Assert.False(player.IsActive);
        var readsAtShutdown = audio.Reads;
        await Task.Delay(TimeSpan.FromMilliseconds(150), Token);
        Assert.Equal(readsAtShutdown, audio.Reads);

        Task<Outcome<T>> Send<T>(ICommand<T> command) => bus.SendAsync(command, new(context, Guid.NewGuid()), Token);
        async Task WaitFor(Func<bool> observes)
        {
            for (var attempt = 0; attempt < 250 && !observes(); attempt++)
                await Task.Delay(20, Token);
            Assert.True(observes());
        }
    }

    private async Task<FadeFixture> OpenAsync()
    {
        var library = Guid.NewGuid();
        var session = Guid.NewGuid();
        var store = LiteDbDesktopPlaylistStore.Open(Path.Combine(folder.FullName, $"{session:N}.db"), true);
        try
        {
            var audio = new AudioClock();
            var preferences = new PlaybackPreferenceHandlers(library, store);
            var initial = new PlaybackState(session, 0, new(0, [], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek |
                PlaybackCapabilities.Mixer, TransportStatus.Stopped);
            var dispatcher = preferences.Register(new PlaybackSessionHandlers(initial, DeckId.A, audio, store, fadePreference: preferences)
                .Register(new())).Build();
            var request = new RequestContext(Guid.NewGuid(), null);
            Value(await dispatcher.SendAsync(new SetLocalOverride(PlaybackPreferences.AutomaticFadeDuration,
                new PreferenceValue.Duration(TimeSpan.FromSeconds(6)), 0), new(request, Guid.NewGuid()), Token));
            Value(await dispatcher.SendAsync(new SetMix(session, new MixEdit.AutoFadeEnabled(true)), new(request, Guid.NewGuid()), Token));
            var started = Value(await dispatcher.SendAsync(new EnqueueTracks(session, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
                EnqueueMode.PlayNow), new(request, Guid.NewGuid()), Token));
            return new(store, session, dispatcher, request, started, audio, Token);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public void Dispose() => folder.Delete(recursive: true);

    private static T Value<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;

    private sealed record FadeFixture(LiteDbDesktopPlaylistStore Store, Guid Session, IRequestDispatcher Bus, RequestContext Request,
        PlaybackState Started, AudioClock Audio, CancellationToken Token) : IDisposable
    {
        internal async Task<PlaybackState> Send(ICommand<PlaybackState> command) => Value(await Bus.SendAsync(command, new(Request, Guid.NewGuid()), Token));
        internal Task<PlaybackState> Tick() => Send(new AdvancePlayback(Session));
        internal Task<PlaybackState> StartFade()
        {
            Audio.SetPosition(DeckId.A, 4);
            return Tick();
        }
        public void Dispose() => Store.Dispose();
    }

    private sealed class TestScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            apply();
            return Task.CompletedTask;
        }
    }

    private sealed class AudioClock : IDesktopPlaybackOutput, IPlaybackMixTarget
    {
        private readonly object gate = new();
        private readonly Dictionary<DeckId, DeckState> native = [];
        private int reads;
        internal List<string> Events { get; } = [];
        internal Guid? Missing { get; set; }
        internal int AttemptsForMissing { get; private set; }
        internal int Reads => Volatile.Read(ref reads);
        internal bool HasDeck(DeckId deck) => native.ContainsKey(deck);
        internal TransportStatus Status(DeckId deck) => native[deck].Status;
        internal void SetPosition(DeckId deck, double seconds)
        {
            lock (gate)
                native[deck] = native[deck] with { Position = TimeSpan.FromSeconds(seconds) };
        }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token)
        {
            lock (gate)
            {
                if (trackId == Missing)
                {
                    AttemptsForMissing++;
                    return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Invalid([new("track-not-available")]));
                }
                Events.Add($"{deck}:{status}");
                native[deck] = new(deck, trackId, startPosition ?? native[deck].Position, TimeSpan.FromSeconds(10), status, true, 1);
                return ReadAsync(deck, trackId, token);
            }
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token)
        {
            lock (gate)
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(native[deck]));
            }
        }

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token)
        {
            lock (gate)
            {
                native[deck] = native[deck] with { Position = position };
                return ReadAsync(deck, trackId, token);
            }
        }

        public Task<Outcome<bool>> ApplyMixAsync(PlaybackMixSettings settings, bool abandonAutomaticFade, CancellationToken token) =>
            Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true));

        public void Dispose() { }
    }
}
