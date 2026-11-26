using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;
using SharedLibraryDefaults = BeetsMan.Host.Contracts.V1.SharedLibraryDefaults;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs the STB07 notification workflow against application routes and real LiteDB.</summary>
/// <remarks>2026-11-25: Verify the reviewed before-and-after values and both compare-and-set revisions for FR-SETTINGS-001.</remarks>
public sealed class NotificationSettingsIntegrationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-notification-settings-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    /// <summary>Uses real settings and engine outcomes to verify independent alert flags and silent historical share status.</summary>
    /// <remarks>2026-11-25: Bind STB07 to FR-SETTINGS-001#ac-precedence and FR-FILESYNC-001#ac-safety without creating any views.</remarks>
    [Fact]
    public async Task DesktopAlertsUseCurrentSettingsAndOnlyNewShareFailures()
    {
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var track = Guid.NewGuid();
        var media = new AlertMediaFixture(library);
        using var store = LiteDbDesktopPlaylistStore.Open(Path.Combine(root.FullName, "alerts.db"), allowCreate: true);
        var preferences = new PlaybackPreferenceHandlers(library, store);
        var engine = new FailingEngine();
        var playback = new PlaybackState(sessionId, 0, new(0, [new(Guid.NewGuid(), track)], false),
            PlaybackCapabilities.Transport, TransportStatus.Stopped);
        var session = new PlaybackSessionHandlers(playback, DeckId.A, engine);
        var dispatcher = new ObservedPreferenceReads(session.Register(preferences.Register(new RequestDispatcher.Builder())
            .RegisterQuery<GetLibraryBatchMode, LibraryBatchMode>(media)
            .RegisterQuery<GetMediaSyncState, MediaSyncState>(media)).Build());
        var shell = new ShellState(Guid.NewGuid(), 0, new(Workspace.Library), PresentationMode.Normal, PresentationMode.Normal,
            sessionId, []);
        var models = await DesktopPresentation.StartAsync(new(dispatcher, new InlineScheduler(), origin), shell, null, playback,
            library, origin, token, session);
        try
        {
            var alerts = new ConcurrentQueue<DesktopAlert>();
            var raised = Channel.CreateUnbounded<DesktopAlert>();
            models.Alerts.Raised += alert => { alerts.Enqueue(alert); raised.Writer.TryWrite(alert); };
            await models.Settings.OpenCategoryCommand.ExecuteAsync(NotificationPreferences.Category);
            await SetAsync(NotificationPreferences.PlaybackErrors, false);
            await models.Player.PlayPauseCommand.ExecuteAsync(null);
            var disabledPlayback = await ReadFlagAsync(NotificationPreferences.PlaybackErrors);
            Assert.Equal("fixture-audio-before", models.Player.Failure?.Code);

            await models.Sync.LoadCommand.ExecuteAsync(null);
            media.Transition(MediaSharePhase.Failed);
            await models.Sync.LoadCommand.ExecuteAsync(null);
            var peer = await raised.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(media.CheckedShare, peer.SubjectId);
            var activePeer = await ReadFlagAsync(NotificationPreferences.PeerNeedsAttention);
            media.Transition(MediaSharePhase.Failed);
            await models.Sync.LoadCommand.ExecuteAsync(null);

            await SetAsync(NotificationPreferences.PeerNeedsAttention, false);
            media.Transition(MediaSharePhase.Ready);
            await models.Sync.LoadCommand.ExecuteAsync(null);
            media.Transition(MediaSharePhase.Failed);
            await models.Sync.LoadCommand.ExecuteAsync(null);
            var disabledPeer = await ReadFlagAsync(NotificationPreferences.PeerNeedsAttention);

            await SetAsync(NotificationPreferences.PlaybackErrors, true);
            engine.Code = "fixture-audio-after";
            await models.Player.PlayPauseCommand.ExecuteAsync(null);
            var audio = await raised.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(track, audio.SubjectId);
            var activePlayback = await ReadFlagAsync(NotificationPreferences.PlaybackErrors);
            DesktopAlertAssertions.Verify(new([.. alerts.Select(alert => $"{alert.Kind}:{alert.Code}")],
                !disabledPlayback && activePlayback, !disabledPeer && activePeer, !dispatcher.Keys.Reader.TryRead(out _)));
        }
        finally { await models.StopAsync(); }

        async Task SetAsync(string key, bool setting)
        {
            await models.Settings.ApplyNotificationsCommand.ExecuteAsync(new PreferenceBatch(NotificationPreferences.Category,
                ImmutableDictionary<string, PreferenceValue>.Empty.Add(key, new PreferenceValue.Flag(setting)), models.Settings.Category!.Revision));
            Assert.Null(models.Settings.Failure);
        }

        async Task<bool> ReadFlagAsync(string key)
        {
            var setting = await dispatcher.Keys.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(key, setting.Key);
            return Assert.IsType<PreferenceValue.Flag>(setting.Value).Value;
        }
    }

    [Fact]
    public async Task ResetRequiresTheReviewedLocalAndSharedRevisionsAndSurvivesRestart()
    {
        var path = Path.Combine(root.FullName, "settings.db");
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        PreferenceResetAssertions.Row[] rows;
        string localConflict, hostConflict;
        bool preserved;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var repository = (IDesktopPreferenceStore)store;
            Value(await Cache(10, true));
            var dispatcher = new PlaybackPreferenceHandlers(library, repository).Register(new()).Build();
            using var settings = new SettingsViewModel(new(dispatcher, new InlineScheduler(), origin), Guid.NewGuid());
            await settings.OpenCategoryCommand.ExecuteAsync(NotificationPreferences.Category);
            var category = Assert.IsType<PreferenceCategorySnapshot>(settings.Category);
            Assert.Equal(PreferenceSource.HostDefault, Setting(category, NotificationPreferences.SyncCompleted).Source);
            var draft = ImmutableDictionary<string, PreferenceValue>.Empty
                .Add(NotificationPreferences.SyncCompleted, new PreferenceValue.Flag(false))
                .Add(NotificationPreferences.PlaybackErrors, new PreferenceValue.Flag(false));
            await settings.ApplyNotificationsCommand.ExecuteAsync(new PreferenceBatch(NotificationPreferences.Category, draft, category.Revision));
            var unrelated = Value(await Send(new SetLocalOverride(PlaybackPreferences.ResumeAfterRestart, new PreferenceValue.Flag(true), 0)));
            Assert.Equal(PreferenceSource.LocalOverride, unrelated.Source);
            var original = await Preview();
            rows = original.Inherited.Zip(original.Local, (after, before) =>
                new PreferenceResetAssertions.Row(after.Key, ((PreferenceValue.Flag)before.Value).Value,
                    ((PreferenceValue.Flag)after.Value).Value, after.Source.ToString())).ToArray();
            Assert.Equal(10, original.HostRevision);
            Assert.IsType<Outcome<PreferenceCategorySnapshot>.Invalid>(await dispatcher.SendAsync(
                new ResetLocalPreferences(original.PlanId, original.Revision), new(context with { OriginInstanceId = Guid.NewGuid() }, Guid.NewGuid()), token));
            var intervened = Value(await Send(new SetLocalOverride(NotificationPreferences.PeerNeedsAttention, new PreferenceValue.Flag(false),
                original.Revision)));
            await settings.ConfirmResetCommand.ExecuteAsync(null);
            localConflict = Assert.IsType<PresentationFailure>(settings.Failure).Code!;
            var whileStale = await NowAsync();
            preserved = !Flag(whileStale, NotificationPreferences.PlaybackErrors) && !Flag(whileStale, NotificationPreferences.SyncCompleted) &&
                !Flag(whileStale, NotificationPreferences.PeerNeedsAttention);
            Value(await Send(new ClearLocalOverride(NotificationPreferences.PeerNeedsAttention, intervened.Revision)));

            var second = await Preview();
            Value(await Cache(11, false));
            await settings.ConfirmResetCommand.ExecuteAsync(null);
            hostConflict = Assert.IsType<PresentationFailure>(settings.Failure).Code!;
            var afterHostChange = await NowAsync();
            Assert.Equal(second.Revision, afterHostChange.Revision);
            Assert.Equal(PreferenceSource.LocalOverride, Setting(afterHostChange, NotificationPreferences.SyncCompleted).Source);
            Value(await Cache(12, true));
            var current = await Preview();
            Assert.NotEqual(original.PlanId, current.PlanId);
            Assert.IsType<Outcome<PreferenceCategorySnapshot>.Invalid>(await Send(new ResetLocalPreferences(second.PlanId, second.Revision)));
            await settings.ConfirmResetCommand.ExecuteAsync(null);
            var cleared = Assert.IsType<PreferenceCategorySnapshot>(settings.Category);
            Assert.Equal(current.Revision + 1, cleared.Revision);
            Assert.Null(settings.ResetPlan);
            Assert.Null(settings.Failure);
            Assert.False(settings.IsInteractionBlocked);
            Assert.Equal(12, cleared.HostRevision);
            Assert.All(cleared.Preferences, item => Assert.NotEqual(PreferenceSource.LocalOverride, item.Source));
            var noOverrides = await Preview();
            Assert.Empty(noOverrides.Inherited);
            Assert.False(settings.ConfirmResetCommand.CanExecute(null));
            var operation = Guid.NewGuid();
            var reset = new ResetLocalPreferences(noOverrides.PlanId, noOverrides.Revision);
            var receipt = Value(await dispatcher.SendAsync(reset, new(context, operation), token));
            Assert.Same(receipt, Value(await dispatcher.SendAsync(reset, new(context, operation), token)));

            async Task<PreferenceResetPlan> Preview()
            {
                await settings.PreviewResetCommand.ExecuteAsync(NotificationPreferences.Category);
                return Assert.IsType<PreferenceResetPlan>(settings.ResetPlan);
            }
            Task<Outcome<T>> Send<T>(ICommand<T> command) => dispatcher.SendAsync(command, new(context, Guid.NewGuid()), token);
            async Task<PreferenceCategorySnapshot> NowAsync() =>
                Value(await dispatcher.QueryAsync(new GetPreferenceCategory(NotificationPreferences.Category), context, token));
            Task<Outcome<StoredPreferences>> Cache(long revision, bool enabled) => repository.CacheSharedPreferenceAsync(library, revision,
                new(NotificationPreferences.SyncCompleted, SharedLibraryDefaults.RegistryVersion, null, enabled), token);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var offline = new PlaybackPreferenceHandlers(library, reopened).Register(new()).Build();
        var persisted = Value(await offline.QueryAsync(new GetPreferenceCategory(NotificationPreferences.Category), context, token));
        var sync = Setting(persisted, NotificationPreferences.SyncCompleted);
        var errors = Setting(persisted, NotificationPreferences.PlaybackErrors);
        var restart = Value(await offline.QueryAsync(new GetPreference(PlaybackPreferences.ResumeAfterRestart), context, token));
        PreferenceResetAssertions.Verify(new(rows, localConflict, hostConflict, preserved,
            ((PreferenceValue.Flag)sync.Value).Value && sync.Source == PreferenceSource.HostDefault,
            ((PreferenceValue.Flag)errors.Value).Value && errors.Source == PreferenceSource.BuiltIn,
            ((PreferenceValue.Flag)restart.Value).Value && restart.Source == PreferenceSource.LocalOverride, persisted.HostRevision == 12));
    }

    [Fact]
    public async Task DesktopCompositionExposesNotificationResetCommands()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        await using var desktop = new DesktopStartup(defaults, [], Guid.NewGuid(), Guid.NewGuid(), _ => new UnusedOutput(),
            presentationScheduler: new InlineScheduler());
        var activated = Value(await desktop.StartAsync(token));
        var model = Assert.IsType<DesktopPresentation>(activated.Presentation).Settings;
        await model.OpenCategoryCommand.ExecuteAsync(NotificationPreferences.Category);
        var state = Assert.IsType<PreferenceCategorySnapshot>(model.Category);
        Assert.Equal(3, state.Preferences.Length);
        await model.PreviewResetCommand.ExecuteAsync(NotificationPreferences.Category);
        Assert.Empty(Assert.IsType<PreferenceResetPlan>(model.ResetPlan).Inherited);
        Assert.False(model.ConfirmResetCommand.CanExecute(null));
    }

    private static EffectivePreference Setting(PreferenceCategorySnapshot category, string key) => category.Preferences.Single(item => item.Key == key);
    private static bool Flag(PreferenceCategorySnapshot category, string key) => ((PreferenceValue.Flag)Setting(category, key).Value).Value;
    private static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    public void Dispose() => root.Delete(recursive: true);

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>Records actual effective-value lookups to order alert tests without sleep-based assertions.</summary>
    /// <remarks>2026-11-25: Observe the FR-SETTINGS-001#ac-precedence preference route.</remarks>
    private sealed class ObservedPreferenceReads(IRequestDispatcher inner) : IRequestDispatcher
    {
        internal Channel<EffectivePreference> Keys { get; } = Channel.CreateUnbounded<EffectivePreference>();

        public Task<Outcome<T>> SendAsync<T>(ICommand<T> request, CommandContext context, CancellationToken ct) => inner.SendAsync(request, context, ct);

        public async Task<Outcome<T>> QueryAsync<T>(IQuery<T> request, RequestContext context, CancellationToken ct)
        {
            var outcome = await inner.QueryAsync(request, context, ct);
            if (request is GetPreference && outcome is Outcome<T>.Success { Value: EffectivePreference setting })
                Keys.Writer.TryWrite(setting);
            return outcome;
        }
    }

    /// <summary>Supplies deliberate current and historical share conditions behind the typed directory route.</summary>
    /// <remarks>2026-11-25: Exercise the transition identity for FR-FILESYNC-001#ac-safety.</remarks>
    private sealed class AlertMediaFixture(Guid library) : IQueryHandler<GetLibraryBatchMode, LibraryBatchMode>,
        IQueryHandler<GetMediaSyncState, MediaSyncState>
    {
        private readonly Guid peer = Guid.NewGuid();
        private readonly Guid previouslyFailed = Guid.NewGuid();
        private long revision;
        private MediaSharePhase current = MediaSharePhase.Ready;
        internal Guid CheckedShare { get; } = Guid.NewGuid();

        internal void Transition(MediaSharePhase next) { current = next; revision++; }

        public Task<Outcome<LibraryBatchMode>> HandleAsync(GetLibraryBatchMode query, RequestContext context, CancellationToken token) =>
            Task.FromResult<Outcome<LibraryBatchMode>>(new Outcome<LibraryBatchMode>.Success(new(library, null, 0, 0, false)));

        public Task<Outcome<MediaSyncState>> HandleAsync(GetMediaSyncState query, RequestContext context, CancellationToken token) =>
            Task.FromResult<Outcome<MediaSyncState>>(new Outcome<MediaSyncState>.Success(new(revision, [new(peer, "fixture", MediaPeerKind.PcMirror, true, 1)],
            [
                new(previouslyFailed, peer, false, revision) { Progress = new(MediaSharePhase.Failed, null, null, "historical-fixture") },
                new(CheckedShare, peer, false, revision) { Progress = new(current, null, null, current == MediaSharePhase.Failed ? "fixture-share-failed" : null) }
            ])));
    }

    /// <summary>Reports a deliberate dependency failure through the real playback handler.</summary>
    /// <remarks>2026-11-25: Exercise a failed track start for FR-PLAYER-001#ac-transport and FR-SETTINGS-001#ac-precedence.</remarks>
    private sealed class FailingEngine : IPlaybackTransportTarget
    {
        internal string Code { get; set; } = "fixture-audio-before";

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token) =>
            Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.DependencyFailure(Code, true));
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) => throw new NotSupportedException();
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnusedOutput : IDesktopPlaybackOutput
    {
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token) =>
            throw new InvalidOperationException("Settings do not use the playback device.");
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid track, CancellationToken token) =>
            throw new InvalidOperationException("Settings do not use the playback device.");
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken token) =>
            throw new InvalidOperationException("Settings do not use the playback device.");
        public void Dispose() { }
    }
}
