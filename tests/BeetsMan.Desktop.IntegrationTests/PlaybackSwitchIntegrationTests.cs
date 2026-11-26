using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Adapters.Playback;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs strategy proposals and confirmation through the active desktop and a real CLI session.</summary>
/// <remarks>2026-11-25: Bind FR-CLI-001#ac-switch and TEST-CLI-001/002 to queue retention, rejection and process ownership.</remarks>
public sealed partial class CLIAcceptanceTests
{
    /// <summary>Exercises SoundFlow with a missing native library and proves an explicit CLI fallback can play.</summary>
    /// <remarks>2026-11-26: Bind the real unavailable-output handling for FR-PLAYER-001#ac-transport and FR-CLI-001#ac-switch.</remarks>
    [Fact]
    public async Task MissingNativeBackendKeepsTransportAndStrategySelectionUsable()
    {
        var music = directory.CreateSubdirectory("missing-native").FullName;
        const string relative = "fixture track.mp3";
        var media = Path.Combine(music, relative);
        await File.WriteAllBytesAsync(media, [1], token);
        var settings = JsonBootstrapFile.ResolveDefaults(directory.FullName, music);
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var track = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var attempts = 0;
        await using var desktop = new DesktopStartup(settings, [], library, origin, catalog => new SoundFlowPlaybackOutput(catalog, () =>
        {
            attempts++;
            throw new DllNotFoundException("Simulated missing native audio library.");
        }));
        Success(await desktop.StartAsync(token));
        Success(await desktop.StopAsync(token));
        using (var fixture = new LiteDatabase(settings.DatabasePath))
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = track, ["LibraryId"] = library, ["RelativePath"] = relative, ["Revision"] = 1L, ["Title"] = "Fallback" });

        var ready = Success(await desktop.StartAsync(token));
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(ready.Workspace);
        var sessionId = Assert.IsType<Guid>(ready.PlaybackSessionId);
        var queued = Success(await Send(new EnqueueTracks(sessionId, [track], EnqueueMode.Append)));
        Assert.Equal(0, attempts);
        var failure = Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(await Send(new Transport(sessionId, TransportAction.Play)));
        var retained = Success(await bus.QueryAsync(new GetPlaybackState(sessionId), request, token));
        var marker = Path.Combine(directory.FullName, "fallback-started");
        var profile = CliRuntimeIntegrationTests.FixtureProfile(CliInvocationMode.Session, media);
        profile = profile with { Revision = 0, Arguments = profile.Arguments.Add("-StartupMarker").Add(marker) };
        Success(await Send(new SaveCliProfile(profile, 0)));
        var proposal = Success(await bus.QueryAsync(new GetPlaybackStrategyProposal(sessionId, PlaybackStrategyIds.Cli, profile.ProfileId), request, token));
        var switched = Success(await Send(new ChangePlaybackStrategy(sessionId, proposal.ProposalId, proposal.Revision)));
        var waits = switched.Status == TransportStatus.Stopped && !File.Exists(marker);
        var playing = Success(await Send(new Transport(sessionId, TransportAction.Play)));
        PlaybackSwitchAssertions.VerifyUnavailable(failure.Code, failure.Retryable,
            queued.Queue.Entries.SequenceEqual(retained.Queue.Entries) && retained.Status == TransportStatus.Stopped,
            proposal.RequiresConfirmation && proposal.Capabilities.HasFlag(PlaybackCapabilities.Transport), waits,
            playing.StrategyId == PlaybackStrategyIds.Cli && playing.Status == TransportStatus.Playing && playing.ActiveTrackId == track && File.Exists(marker),
            attempts);

        Task<Outcome<T>> Send<T>(ICommand<T> command) => bus.SendAsync(command, new(request, Guid.NewGuid()), token);
    }

    /// <summary>Stops the prior engine, preserves the queue and waits for explicit Play through both switches.</summary>
    /// <remarks>2026-11-26: Bind the named production strategy case for FR-CLI-001#ac-switch.</remarks>
    [Fact]
    public async Task Switch_MatchesContract()
    {
        var music = directory.CreateSubdirectory("music").FullName;
        const string relative = "repeat entry with spaces.mp3";
        var media = Path.Combine(music, relative);
        await File.WriteAllBytesAsync(media, [1], token);
        var marker = Path.Combine(directory.FullName, "worker-started");
        var settings = JsonBootstrapFile.ResolveDefaults(directory.FullName, music);
        var native = new NativeFixture();
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var track = Guid.NewGuid();
        var earlier = new QueueEntry(Guid.NewGuid(), track);
        var selected = new QueueEntry(Guid.NewGuid(), track);
        var request = new RequestContext(Guid.NewGuid(), origin);
        await using var desktop = new DesktopStartup(settings, [], library, origin, _ => native, presentationScheduler: new InlineScheduler());
        var first = Success(await desktop.StartAsync(token));
        var session = Assert.IsType<Guid>(first.PlaybackSessionId);
        Success(await desktop.StopAsync(token));
        using (var store = LiteDbDesktopPlaylistStore.Open(settings.DatabasePath, allowCreate: false))
            Success(await store.SaveCheckpointAsync(new(session, 1, 2, [earlier, selected], selected.EntryId, TimeSpan.Zero, false,
                RepeatMode.Off, null), 0, token));
        using (var fixture = new LiteDatabase(settings.DatabasePath))
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = track, ["LibraryId"] = library, ["RelativePath"] = relative, ["Revision"] = 1L, ["Title"] = "Repeat" });

        var reopened = Success(await desktop.StartAsync(token));
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(reopened.Workspace);
        var models = Assert.IsType<DesktopPresentation>(reopened.Presentation);
        var profile = CliRuntimeIntegrationTests.FixtureProfile(CliInvocationMode.Session, media);
        profile = profile with { Revision = 0, Capabilities = PlaybackCapabilities.Transport | PlaybackCapabilities.Equalizer,
            Arguments = profile.Arguments.Add("-StartupMarker").Add(marker),
            Actions = [.. profile.Actions.Select(action => action.Action == CliPlaybackAction.Status ? action with { RequestKind = "timeout" } : action)] };
        Success(await Send(new SaveCliProfile(profile, 0)));
        var initial = Success(await Send(new Transport(session, TransportAction.Play)));
        Assert.Equal(selected.EntryId, initial.ActiveQueueEntryId);
        // 2026-11-25: Check FR-PLAYER-001#ac-modes against a strategy that produces no visualization feed.
        Assert.True((await Visualization()).Supported);
        var stale = await Proposal(PlaybackStrategyIds.Cli, profile.ProfileId);
        var updated = profile with { Revision = 1, Timeout = TimeSpan.FromSeconds(4) };
        Success(await Send(new SaveCliProfile(updated, 1)));
        Assert.Equal("stale-cli-profile", Assert.IsType<Outcome<PlaybackState>.Conflict>(
            await Send(new ChangePlaybackStrategy(session, stale.ProposalId, stale.Revision))).Code);
        Assert.False(File.Exists(marker));

        await models.CliPlayback.ChooseEngineCommand.ExecuteAsync(new PlaybackStrategySelection(PlaybackStrategyIds.Cli, profile.ProfileId));
        var displayed = Assert.IsType<PlaybackStrategyProposal>(models.CliPlayback.Proposal);
        native.Position = TimeSpan.FromMilliseconds(250);
        var advanced = Success(await Send(new AdvancePlayback(session)));
        Assert.True(advanced.Revision > displayed.Revision);
        native.RejectStop = true;
        var rejected = await Send(new ChangePlaybackStrategy(session, displayed.ProposalId, displayed.Revision));
        Assert.Equal("fixture-stop-rejected", Assert.IsType<Outcome<PlaybackState>.DependencyFailure>(rejected).Code);
        Assert.Equal(TransportStatus.Playing, Success(await bus.QueryAsync(new GetPlaybackState(session), request, token)).Status);
        Assert.False(File.Exists(marker));
        native.RejectStop = false;

        await models.CliPlayback.SwitchCommand.ExecuteAsync(null);
        var switched = Assert.IsType<PlaybackState>(models.Playback.State);
        PlaybackSwitchAssertions.Verify(advanced, switched, PlaybackCapabilities.Transport, [.. native.Actions], File.Exists(marker) ? 1 : 0);
        Assert.Equal(PlaybackStrategyIds.Cli, switched.StrategyId);
        Assert.Equal(profile.ProfileId, switched.StrategyProfileId);
        Assert.Null(models.CliPlayback.Proposal);
        Assert.False(models.Player.ApplyEqualizerCommand.CanExecute(ImmutableArray<double>.Empty));
        Assert.False((await Visualization()).Supported);
        Assert.Null((await Visualization()).Frame);
        Assert.Equal("visualization-unavailable", Assert.Single(Assert.IsType<Outcome<VisualizationState>.Invalid>(
            await Send(new SelectVisualization(session, VisualizationChoice.Waveform))).Issues).Code);
        var process = Success(await Send(new Transport(session, TransportAction.Play)));
        CliRuntimeAssertions.Verify(new Outcome<DeckState>.Success(Assert.Single(process.Decks)), track, TransportStatus.Playing, 0);
        Assert.Equal(selected.EntryId, process.ActiveQueueEntryId);
        var workerId = int.Parse(await File.ReadAllTextAsync(marker, token), CultureInfo.InvariantCulture);
        using var worker = Process.GetProcessById(workerId);
        Assert.False(worker.HasExited);
        var poll = await Send(new AdvancePlayback(session));
        Assert.IsType<Outcome<PlaybackState>.Success>(poll);
        var nativePollIssuedCliStatus = worker.HasExited;

        var back = await Proposal(PlaybackStrategyIds.SoundFlow, null);
        var operation = Guid.NewGuid();
        var command = new ChangePlaybackStrategy(session, back.ProposalId, back.Revision);
        var reverted = Success(await Send(command, operation));
        Assert.True(worker.HasExited);
        Assert.Equal(reverted, Success(await Send(command, operation)));
        Assert.Equal(PlaybackStrategyIds.SoundFlow, reverted.StrategyId);
        Assert.Equal(TransportStatus.Stopped, reverted.Status);
        Assert.True((await Visualization()).Supported);
        Assert.Null((await Visualization()).Frame);
        Assert.Equal(selected.EntryId, reverted.ActiveQueueEntryId);
        Assert.Equal(new[] { "Play", "Stop" }, native.Actions);
        Assert.False((await Proposal(PlaybackStrategyIds.SoundFlow, null)).RequiresConfirmation);
        var resumed = Success(await Send(new Transport(session, TransportAction.Play)));
        Assert.Equal(selected.EntryId, resumed.ActiveQueueEntryId);
        Assert.Equal(new[] { "Play", "Stop", "Play" }, native.Actions);

        var stoppedFile = Path.Combine(directory.FullName, "one-shot-stopped");
        var oneShot = CliRuntimeIntegrationTests.FixtureProfile(CliInvocationMode.OneShot, media);
        oneShot = oneShot with { Revision = 0, Arguments = oneShot.Arguments.Add("-StopMarker").Add(stoppedFile) };
        Success(await Send(new SaveCliProfile(oneShot, 0)));
        var next = await Proposal(PlaybackStrategyIds.Cli, oneShot.ProfileId);
        Success(await Send(new ChangePlaybackStrategy(session, next.ProposalId, next.Revision)));
        Success(await Send(new Transport(session, TransportAction.Play)));
        Assert.False(File.Exists(stoppedFile));
        Success(await desktop.StopAsync(token));
        using var recoveredStore = LiteDbDesktopPlaylistStore.Open(settings.DatabasePath, allowCreate: false);
        var checkpoint = Assert.IsType<PlaybackCheckpoint>(Success(await recoveredStore.ReadCheckpointAsync(session, token)));
        PlaybackSwitchAssertions.VerifyLifecycle(nativePollIssuedCliStatus, checkpoint.WasPlaying,
            File.Exists(stoppedFile) && await File.ReadAllTextAsync(stoppedFile, token) == "stop");
        Assert.Equal(selected.EntryId, checkpoint.ActiveEntryId);

        async Task<PlaybackStrategyProposal> Proposal(string engine, Guid? id) =>
            Success(await bus.QueryAsync(new GetPlaybackStrategyProposal(session, engine, id), request, token));
        async Task<VisualizationState> Visualization() => Success(await bus.QueryAsync(new GetVisualization(session), request, token));
        Task<Outcome<T>> Send<T>(ICommand<T> value, Guid? operationId = null) =>
            bus.SendAsync(value, new(request, operationId ?? Guid.NewGuid()), token);
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class NativeFixture : IDesktopPlaybackOutput, IPlaybackVisualizationSource
    {
        private DeckState? deck;
        internal List<string> Actions { get; } = [];
        internal TimeSpan Position { get; set; }
        internal bool RejectStop { get; set; }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deckId, Guid track, TimeSpan? position, TransportStatus status, CancellationToken ct)
        {
            if (status == TransportStatus.Stopped && RejectStop)
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.DependencyFailure("fixture-stop-rejected", true));
            Actions.Add(status == TransportStatus.Playing ? "Play" : "Stop");
            Position = position ?? Position;
            deck = new(deckId, track, Position, TimeSpan.FromSeconds(30), status, true, 1);
            return ReadAsync(deckId, track, ct);
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deckId, Guid track, CancellationToken ct) =>
            Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(deck! with { Position = Position }));

        public Task<Outcome<DeckState>> SeekAsync(DeckId deckId, Guid track, TimeSpan position, CancellationToken ct) => ReadAsync(deckId, track, ct);

        public PlaybackVisualizationFrame? ReadLatest(long afterSequence) => null;

        public void Dispose() => deck = null;
    }
}
