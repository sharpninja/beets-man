using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises the restart toggle through the real desktop preference store and playback lifecycle commands.</summary>
/// <remarks>2026-11-25: Bind TEST-PLAYER-002 for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public sealed class PlaybackRestartIntegrationTests
{
    [Fact]
    public async Task RestartResume_MatchesContract()
    {
        var root = Directory.CreateTempSubdirectory("beets-playback-restart-");
        try { await VerifyAsync(root.FullName, TestContext.Current.CancellationToken); }
        finally { root.Delete(recursive: true); }
    }

    /// <summary>Exercises each preference tier across a disconnected reopen.</summary>
    /// <remarks>2026-11-26: Reuse this fixture for the named FR-SETTINGS-001#ac-precedence case.</remarks>
    internal static async Task PlaybackPreferenceLayerPreservesLocalOverrideAndLastSharedDefault()
    {
        var root = Directory.CreateTempSubdirectory("beets-playback-preferences-");
        try
        {
            var token = TestContext.Current.CancellationToken;
            var path = Path.Combine(root.FullName, "preferences.db");
            var library = Guid.NewGuid();
            var origin = Guid.NewGuid();
            var context = new RequestContext(Guid.NewGuid(), origin);
            var host = new PreferenceHost(library, origin);
            var fade = PlaybackPreferences.AutomaticFadeDuration;
            using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
            {
                var bus = new PlaybackPreferenceHandlers(library, store, host).Register(new()).Build();
                var initial = Success(await bus.QueryAsync(new GetPreference(fade), context, token));
                Assert.Equal(new PreferenceValue.Duration(TimeSpan.Zero), initial.Value);
                Assert.Equal(PreferenceSource.BuiltIn, initial.Source);
                var shared = ImmutableDictionary<string, PreferenceValue>.Empty.Add(fade, new PreferenceValue.Duration(TimeSpan.FromSeconds(4)));
                var published = Success(await bus.SendAsync(new SetSharedPreferences(PlaybackPreferences.Category, shared, 0),
                    new(context, Guid.NewGuid()), token));
                Assert.Equal(1, published.HostRevision);
                Assert.Equal(new LibraryRecordIntent.SharedDefault(fade, TimeSpan.FromSeconds(4).Ticks, 0), host.LastChange);
                var overrides = ImmutableDictionary<string, PreferenceValue>.Empty
                    .Add(PlaybackPreferences.ResumeAfterRestart, new PreferenceValue.Flag(true))
                    .Add(fade, new PreferenceValue.Duration(TimeSpan.FromSeconds(2)));
                var committed = Success(await bus.SendAsync(new SetLocalPreferences(PlaybackPreferences.Category, overrides, published.Revision),
                    new(context, Guid.NewGuid()), token));
                Assert.All(committed.Preferences, value => Assert.Equal(PreferenceSource.LocalOverride, value.Source));
                Assert.IsType<Outcome<EffectivePreference>.Conflict>(await bus.SendAsync(new ClearLocalOverride(fade, 0), new(context, Guid.NewGuid()), token));
            }
            using (var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
            {
                var bus = new PlaybackPreferenceHandlers(library, reopened).Register(new()).Build();
                var local = Success(await bus.QueryAsync(new GetPreference(fade), context, token));
                Assert.Equal(new PreferenceValue.Duration(TimeSpan.FromSeconds(2)), local.Value);
                Assert.Equal(PreferenceSource.LocalOverride, local.Source);
                var inherited = Success(await bus.SendAsync(new ClearLocalOverride(fade, local.Revision), new(context, Guid.NewGuid()), token));
                Assert.Equal(new PreferenceValue.Duration(TimeSpan.FromSeconds(4)), inherited.Value);
                Assert.Equal(PreferenceSource.HostDefault, inherited.Source);
                var resume = Success(await bus.QueryAsync(new GetPreference(PlaybackPreferences.ResumeAfterRestart), context, token));
                Assert.Equal(new PreferenceValue.Flag(true), resume.Value);
                var cleared = Success(await bus.SendAsync(new ClearLocalOverride(resume.Key, resume.Revision), new(context, Guid.NewGuid()), token));
                Assert.Equal(new PreferenceValue.Flag(false), cleared.Value);
                Assert.Equal(PreferenceSource.BuiltIn, cleared.Source);
            }
        }
        finally { root.Delete(recursive: true); }
    }

    private static async Task VerifyAsync(string directory, CancellationToken token)
    {
        var scope = new LibraryRecordScope(Guid.NewGuid(), Guid.NewGuid());
        var session = Guid.NewGuid();
        var track = Guid.NewGuid();
        var first = new QueueEntry(Guid.NewGuid(), track);
        var current = new QueueEntry(Guid.NewGuid(), track);
        var position = TimeSpan.FromSeconds(37);
        var play = new RecordPlay(Guid.NewGuid(), track, new DateTimeOffset(2026, 11, 25, 0, 48, 0, TimeSpan.Zero));
        var host = new PreferenceHost(scope.LibraryId, scope.OriginInstanceId);
        var context = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var initial = new PlaybackState(session, 0, new(0, [], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, TransportStatus.Stopped);
        var queue = new PlaybackCheckpoint(session, 1, 4, [first, current], current.EntryId, position, false, RepeatMode.All,
            new(new(Guid.NewGuid(), 11), "Recent list"), new(play.PlayId, track, current.EntryId), WasPlaying: true);
        var offPath = Path.Combine(directory, "off.db");
        var onPath = Path.Combine(directory, "on.db");
        var pausedPath = Path.Combine(directory, "paused.db");
        var unbackedPath = Path.Combine(directory, "unbacked.db");
        var unavailablePath = Path.Combine(directory, "unavailable.db");
        await CreateAsync(offPath, queue, false);
        await CreateAsync(onPath, queue, true);
        await CreateAsync(pausedPath, queue with { WasPlaying = false }, true);
        await CreateAsync(unbackedPath, queue with { PlaylistBacking = null }, true);
        await CreateAsync(unavailablePath, queue, true);
        var off = new StartupBackend();
        var on = new StartupBackend();
        var paused = new StartupBackend();
        var unbacked = new StartupBackend();
        var disconnected = new StartupBackend { TrackUnavailable = true };
        bool exact;
        bool sameHistory;
        bool recoverable;
        int fromQuery;
        int startsOn;
        int startsOnRetry;
        using (var store = LiteDbDesktopPlaylistStore.Open(offPath, allowCreate: false))
        {
            var bus = Connect(store, off);
            var loaded = Success(await bus.SendAsync(new InitializePlayback(session), Command(), token));
            Assert.Equal(TransportStatus.Stopped, loaded.Status);
            Assert.Equal(current.EntryId, loaded.ActiveQueueEntryId);
            Assert.Equal(position, loaded.Position);
        }
        using (var store = LiteDbDesktopPlaylistStore.Open(onPath, allowCreate: false))
        {
            var bus = Connect(store, on);
            var model = Success(await bus.QueryAsync(new GetPlaybackState(session), context, token));
            Assert.Equal(TransportStatus.Stopped, model.Status);
            fromQuery = on.Starts.Count;
            var startup = Command();
            var resumed = Success(await bus.SendAsync(new InitializePlayback(session), startup, token));
            startsOn = on.Starts.Count;
            exact = resumed.Status == TransportStatus.Playing && resumed.ActiveQueueEntryId == current.EntryId &&
                resumed.Position == position && resumed.Queue.Entries.SequenceEqual([first, current]);
            var checkpoint = Success(await store.ReadCheckpointAsync(session, token))!;
            var history = Success(await ((ILibraryRecordOutboxStore)store).ReadHistoryAsync(scope, 10, token));
            sameHistory = checkpoint.ActivePlay?.PlayId == play.PlayId && Assert.Single(history).PlayId == play.PlayId;
            Success(await bus.SendAsync(new InitializePlayback(session), startup, token));
            Success(await bus.SendAsync(new InitializePlayback(session), Command(), token));
            startsOnRetry = on.Starts.Count;
            on.Advance(TimeSpan.FromSeconds(14));
            Success(await bus.SendAsync(new SavePlaybackForRestart(session), Command(), token));
            var shutdown = Success(await store.ReadCheckpointAsync(session, token))!;
            Assert.Equal(TimeSpan.FromSeconds(51), shutdown.Position);
            Assert.True(shutdown.WasPlaying);
        }
        await CheckRestoreOnly(pausedPath, paused);
        await CheckRestoreOnly(unbackedPath, unbacked);
        using (var store = LiteDbDesktopPlaylistStore.Open(unavailablePath, allowCreate: false))
        {
            var bus = Connect(store, disconnected);
            var failed = await bus.SendAsync(new InitializePlayback(session), Command(), token);
            var loaded = Success(await bus.QueryAsync(new GetPlaybackState(session), context, token));
            recoverable = failed is Outcome<PlaybackState>.DependencyFailure { Retryable: true, Code: "playback-resume-track-unavailable" } &&
                loaded is { Status: TransportStatus.Stopped } && loaded.ActiveQueueEntryId == current.EntryId && loaded.Position == position;
            disconnected.TrackUnavailable = false;
            var retried = Success(await bus.SendAsync(new InitializePlayback(session), Command(), token));
            Assert.Equal(TransportStatus.Playing, retried.Status);
            Assert.Equal(position, retried.Position);
        }

        PlaybackRestartAssertions.Verify(new(off.Starts.Count, startsOn, startsOnRetry, exact, Assert.Single(on.Starts).Position.Ticks,
            sameHistory, paused.Starts.Count, unbacked.Starts.Count, recoverable, fromQuery, host.LastChange is null));

        CommandContext Command() => new(context, Guid.NewGuid());
        async Task CreateAsync(string path, PlaybackCheckpoint checkpoint, bool enabled)
        {
            using var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true);
            Success(await store.SaveCheckpointWithPlayAsync(checkpoint, 0, scope, play, token));
            var settings = new PlaybackPreferenceHandlers(scope.LibraryId, store, host).Register(new()).Build();
            var startup = Success(await settings.QueryAsync(new GetPreference(PlaybackPreferences.ResumeAfterRestart), context, token));
            Assert.Equal(new PreferenceValue.Flag(false), startup.Value);
            if (enabled)
            {
                var changed = Success(await settings.SendAsync(new SetLocalOverride(startup.Key, new PreferenceValue.Flag(true), startup.Revision), Command(), token));
                Assert.Equal(PreferenceSource.LocalOverride, changed.Source);
                Assert.False(changed.CanEditShared);
            }
            var share = ImmutableDictionary<string, PreferenceValue>.Empty.Add(startup.Key, new PreferenceValue.Flag(true));
            Assert.IsType<Outcome<PreferenceCategorySnapshot>.Invalid>(await settings.SendAsync(
                new SetSharedPreferences(PlaybackPreferences.Category, share, 0), Command(), token));
        }
        RequestDispatcher Connect(LiteDbDesktopPlaylistStore store, StartupBackend backend)
        {
            var preferences = new PlaybackPreferenceHandlers(scope.LibraryId, store, host);
            var playback = new PlaybackSessionHandlers(initial, DeckId.A, backend, store, historyScope: scope, restartPreference: preferences);
            return preferences.Register(playback.Register(new RequestDispatcher.Builder())).Build();
        }
        async Task CheckRestoreOnly(string path, StartupBackend backend)
        {
            using var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
            var restored = Success(await Connect(store, backend).SendAsync(new InitializePlayback(session), Command(), token));
            Assert.Equal(TransportStatus.Stopped, restored.Status);
            Assert.Equal(current.EntryId, restored.ActiveQueueEntryId);
        }
    }

    private static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;

    internal sealed class PreferenceHost(Guid library, Guid origin) : ILibraryRecordHost
    {
        internal LibraryRecordIntent? LastChange { get; private set; }

        public Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token)
        {
            LastChange = change;
            var accepted = Assert.IsType<LibraryRecordIntent.SharedDefault>(change);
            var id = LibraryRecordIdentity.SharedDefault(library, accepted.Key);
            return Task.FromResult<Outcome<LibraryRecordReceipt>>(new Outcome<LibraryRecordReceipt>.Success(new(library, origin, operation, id, 1, 1)));
        }
    }

    /// <summary>Supplies deterministic audio positions and the same unavailable-track outcome as the configured adapter.</summary>
    /// <remarks>2026-11-26: Share the transport observation with the legacy checkpoint case for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
    internal sealed class StartupBackend : IPlaybackTransportTarget
    {
        private DeckState? active;
        internal List<(Guid Track, TimeSpan Position)> Starts { get; } = [];
        internal bool TrackUnavailable { get; set; }
        internal void Advance(TimeSpan duration) => active = active! with { Position = active.Position + duration };

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token)
        {
            if (TrackUnavailable)
                return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Invalid([new("track-not-available")]));
            var position = startPosition ?? active?.Position ?? TimeSpan.Zero;
            if (status == TransportStatus.Playing)
                Starts.Add((trackId, position));
            active = new(deck, trackId, position, TimeSpan.FromMinutes(2), status, true, 1);
            return Result();
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) => Result();
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token)
        {
            active = active! with { Position = position };
            return Result();
        }

        private Task<Outcome<DeckState>> Result() => Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(active!));
    }
}
