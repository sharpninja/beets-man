using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Adapters.Playback;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Desktop.Presentation;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Contains bootstrap status and, when ready, the active workspace and its playback startup result.</summary>
/// <remarks>2026-11-25: Expose recovery and the playback lifecycle for FR-CONFIG-001#ac-repair and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
/// <remarks>2026-11-25: Expose the acquired local plugin package boundary for FR-PLUGINS-001#ac-manage.</remarks>
public sealed record DesktopStartupState(BootstrapState Bootstrap, IRequestDispatcher? Workspace = null,
    Guid? PlaybackSessionId = null, Outcome<PlaybackState>? Playback = null, DesktopPresentation? Presentation = null,
    LocalPluginPackages? PluginPackages = null);

/// <summary>Owns the configured playback output for one desktop workspace.</summary>
/// <remarks>2026-11-25: Keep deck transport and disposal together for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public interface IDesktopPlaybackOutput : IPlaybackTransportTarget, IDisposable;

/// <summary>Activates validated desktop services and checkpoints playback before closing their resources.</summary>
/// <remarks>2026-11-25: Compose appsettings, LiteDB, cached catalog and playback for FR-CONFIG-001#ac-defaults, FR-CONFIG-001#ac-repair and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
/// <remarks>2026-11-25: Serve redacted in-memory diagnostics before workspace activation for FR-CONFIG-001#ac-repair and ac-logs.</remarks>
public sealed partial class DesktopStartup : IAsyncDisposable
{
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly BootstrapHandlers bootstrap;
    private readonly DiagnosticsLoggerProvider recoveryDiagnostics;
    private readonly ILogger recoveryLog;
    private readonly Guid library;
    private readonly Guid origin;
    private readonly Func<LiteDbCatalogReader, IDesktopPlaybackOutput> openOutput;
    private readonly Func<bool> isOffline;
    private readonly DesktopHostBinding? host;
    private readonly IUiScheduler? presentationScheduler;
    private readonly bool pluginRecoveryMode;
    private readonly Func<ISystemPlaybackShortcutPlatform>? openShortcutPlatform;
    private readonly ILogFolderBrowser? logFolderBrowser;
    private ActiveDesktop? active;
    private bool disposed;

    /// <summary>Binds local paths and the registered identity. The default port opens the system audio output on demand.</summary>
    public DesktopStartup(BootstrapSettings defaults, IEnumerable<string> sharedRoots, Guid libraryId, Guid originInstanceId,
        Func<LiteDbCatalogReader, IDesktopPlaybackOutput>? openOutput = null, Func<bool>? isOffline = null, DesktopHostBinding? host = null,
        IUiScheduler? presentationScheduler = null, bool pluginRecoveryMode = false,
        Func<ISystemPlaybackShortcutPlatform>? openShortcutPlatform = null, ILogFolderBrowser? logFolderBrowser = null)
    {
        if (libraryId == Guid.Empty || originInstanceId == Guid.Empty)
            throw new ArgumentException("The workspace requires a library and an originating instance.");
        library = libraryId;
        origin = originInstanceId;
        this.host = host;
        this.presentationScheduler = presentationScheduler;
        this.pluginRecoveryMode = pluginRecoveryMode;
        this.openShortcutPlatform = openShortcutPlatform;
        this.logFolderBrowser = logFolderBrowser;
        this.openOutput = openOutput ?? (catalog => new SoundFlowPlaybackOutput(catalog));
        this.isOffline = isOffline ?? (() => host is null);
        bootstrap = new(new JsonBootstrapFile(defaults), new DesktopBootstrapEnvironment(sharedRoots));
        recoveryDiagnostics = new(defaults with { RollingLog = null });
        recoveryLog = recoveryDiagnostics.CreateLogger("Desktop.Bootstrap");
        Bootstrap = bootstrap.Register(new RequestDispatcher.Builder())
            .RegisterQuery<GetDiagnostics, DiagnosticsPage>(recoveryDiagnostics)
            .Build(typeof(RetryDesktopBootstrap), typeof(InitializeDesktop), typeof(GetBootstrapState), typeof(SaveBootstrapSettings),
                typeof(GetDiagnostics));
        if (presentationScheduler is not null)
            Setup = ComposeSetup(presentationScheduler);
    }

    /// <summary>Gets the dispatcher available during first run and configuration repair.</summary>
    public IRequestDispatcher Bootstrap { get; }

    /// <summary>Gets the first-run and repair ViewModels when a presentation scheduler was supplied.</summary>
    /// <remarks>2026-11-25: Bind the explicit setup choice for FR-CONFIG-001#ac-defaults and ac-repair.</remarks>
    public DesktopSetupPresentation? Setup { get; }

    /// <summary>Gets the current workspace after a successful start or setup command.</summary>
    /// <remarks>2026-11-25: Expose the completed handoff for FR-CONFIG-001#ac-repair.</remarks>
    public DesktopStartupState? ActiveWorkspace => Volatile.Read(ref active)?.State;

    /// <summary>Validates and activates the local workspace, then dispatches the playback startup intent once per activation.</summary>
    public async Task<Outcome<DesktopStartupState>> StartAsync(CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (active is { } current)
                    return new Outcome<DesktopStartupState>.Success(current.State);
                var attempt = Attempt();
                var result = await Bootstrap.SendAsync(new RetryDesktopBootstrap(), attempt, token).ConfigureAwait(false);
                if (result is Outcome<BootstrapState>.Success { Value.Status: BootstrapStatus.FirstRun, Value.Proposed: { } defaults })
                {
                    attempt = Attempt();
                    result = await Bootstrap.SendAsync(new InitializeDesktop(defaults), attempt, token).ConfigureAwait(false);
                }
                return await CompleteStartupAsync(result, attempt, token).ConfigureAwait(false);
            }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<DesktopStartupState>.Cancelled();
        }
    }

    /// <summary>Saves playback and releases the workspace. A failed save leaves the same session available for a retry.</summary>
    public async Task<Outcome<PlaybackState?>> StopAsync(CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await StopCoreAsync(token, releaseOnFailure: false).ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<PlaybackState?>.Cancelled();
        }
    }

    /// <summary>Attempts a final checkpoint and releases owned resources, even if the final attempt cannot save.</summary>
    public async ValueTask DisposeAsync()
    {
        await lane.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            try { await StopCoreAsync(CancellationToken.None, releaseOnFailure: true).ConfigureAwait(false); }
            finally
            {
                recoveryDiagnostics.Dispose();
                if (Setup is { } setup && presentationScheduler is { } scheduler)
                    await scheduler.PostAsync(() =>
                    {
                        setup.Diagnostics.Dispose();
                        setup.Configuration.Dispose();
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { lane.Release(); }
    }

    /// <summary>Interprets configuration under the startup lane and opens at most one workspace.</summary>
    /// <remarks>2026-11-25: Share ready, repair and restart handling between both entry points for FR-CONFIG-001#ac-repair.</remarks>
    private async Task<Outcome<DesktopStartupState>> CompleteStartupAsync(Outcome<BootstrapState> result, CommandContext context,
        CancellationToken token)
    {
        if (result is not Outcome<BootstrapState>.Success { Value: var configured })
            return ForwardFailure<BootstrapState, DesktopStartupState>(result);
        if (configured.Status == BootstrapStatus.RepairRequired)
        {
            using var scope = recoveryLog.BeginScope(new DiagnosticScope(context.Request.CorrelationId));
            recoveryLog.LogWarning(new EventId(1001, "ConfigurationRepairRequired"), "Desktop configuration requires repair.");
        }
        if (active is not null || configured is not { Status: BootstrapStatus.Ready, Effective: { } settings })
            return new Outcome<DesktopStartupState>.Success(new(configured));
        return await ActivateAsync(configured, settings, token).ConfigureAwait(false);
    }

    private async Task<Outcome<DesktopStartupState>> ActivateAsync(BootstrapState startup, BootstrapSettings settings, CancellationToken token)
    {
        LiteDbDesktopPlaylistStore store;
        try { store = LiteDbDesktopPlaylistStore.Open(settings.DatabasePath, allowCreate: false); }
        catch (Exception exception) when (exception is LiteException or IOException or UnauthorizedAccessException)
        {
            return new Outcome<DesktopStartupState>.DependencyFailure("desktop-database-unavailable", true);
        }

        IDesktopPlaybackOutput? audio = null;
        PlaybackSessionHandlers? session = null;
        DiagnosticsLoggerProvider? diagnostics = null;
        AndroidArrivalMonitor? arrivals = null;
        DesktopPresentation? presentation = null;
        DesktopPluginMonitor? plugins = null;
        ShortcutHandlers? shortcuts = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var batchScope = new LibraryBatchScope(library, origin);
            var catalog = new LiteDbCatalogReader(store, library, settings.MusicRoot, isOffline, batchScope: batchScope);
            diagnostics = new DiagnosticsLoggerProvider(settings, browser: logFolderBrowser);
            audio = openOutput(catalog);
            var capabilities = PlaybackCapabilities.Transport | PlaybackCapabilities.Seek;
            if (audio is IPlaybackMixTarget)
                capabilities |= PlaybackCapabilities.Volume | PlaybackCapabilities.Mixer | PlaybackCapabilities.Equalizer;
            if (audio is IPlaybackVisualizationSource)
                capabilities |= PlaybackCapabilities.Visualization;
            var playback = new PlaybackState(store.StoreId, 0, new(0, [], false), capabilities, TransportStatus.Stopped);
            var routes = bootstrap.Register(new RequestDispatcher.Builder())
                .RegisterQuery<GetDiagnostics, DiagnosticsPage>(diagnostics)
                .RegisterCommand<OpenLogFolder, FolderOpenReceipt>(diagnostics)
                .RegisterQuery<BrowseCatalog, CatalogPage>(catalog)
                .RegisterQuery<GetTrackAvailability, TrackAvailabilitySnapshot>(catalog);
            // 2026-11-25: Bind local YAML profile commands for FR-CLI-001#ac-profiles.
            var cliProfiles = new YamlCliProfileStore(settings.SettingsPath);
            new CliProfileHandlers(cliProfiles).Register(routes);
            // 2026-11-25: Register local-only versioned plugin management for FR-PLUGINS-001#ac-manage.
            var pluginPackages = new LocalPluginPackages(settings.SettingsPath);
            plugins = new(pluginPackages, pluginRecoveryMode, diagnostics.CreateLogger("Desktop.Plugins"));
            new PluginRegistryHandlers(store, pluginPackages, plugins.Supervisor).Register(routes);
            var (queue, preferences, playlists) = RegisterLibrary(routes, store, catalog, batchScope);
            // 2026-11-25: Bind local Settings commands and opt-in platform lifetime for FR-SETTINGS-001#ac-shortcuts.
            var shortcutPlatform = openShortcutPlatform?.Invoke() ?? CreateShortcutPlatform();
            shortcuts = new(store, shortcutPlatform);
            shortcuts.Register(routes);
            var media = RegisterMediaSync(routes);
            var shell = new ShellState(Guid.NewGuid(), 0, new(Workspace.Library), PresentationMode.Normal, PresentationMode.Normal,
                playback.SessionId, []);
            if (presentationScheduler is not null && media is not null)
                arrivals = new AndroidArrivalMonitor(media, origin);
            new ShellHandlers(shell, arrivals).Register(routes);
            // 2026-11-25: Select a validated local runtime for FR-CLI-001#ac-switch.
            session = new PlaybackSessionHandlers(playback, DeckId.A, audio, queuePlaylists: queue,
                historyScope: new LibraryRecordScope(library, origin), restartPreference: preferences, fadePreference: preferences,
                cli: new(cliProfiles, profile => new CliPlaybackOutput(profile, catalog.ResolveTrackAsync)));
            IRequestDispatcher dispatcher = null!;
            // 2026-11-25: Route approved plugin actions through the same dispatcher for FR-PLUGINS-001#ac-visualize.
            new PluginActionHandlers(plugins.Supervisor, playback.SessionId, () => dispatcher).Register(routes);
            var visualization = new VisualizationSession(playback.SessionId, audio as IPlaybackVisualizationSource,
                session.ReadVisualizationContext, plugins.Supervisor);
            visualization.Register(routes);
            dispatcher = session.Register(routes).Build(typeof(GetPlaybackState), typeof(InitializePlayback),
                typeof(GetDiagnostics), typeof(OpenLogFolder),
                typeof(SavePlaybackForRestart), typeof(BrowseCatalog), typeof(GetPreference), typeof(GetPreferenceCategory),
                typeof(GetPreferenceResetPlan), typeof(ResetLocalPreferences), typeof(GetLibraryBatchReview),
                typeof(SavePlaylist), typeof(EditQueue), typeof(ResumeReplication), typeof(RefreshReplication), typeof(AdvancePlayback),
                typeof(GetRegisteredInstances), typeof(RegisterInstance), typeof(RevokeInstance),
                typeof(GetHostLibraryState), typeof(GetHostLibraryConfiguration), typeof(InitializeHostLibrary), typeof(SaveHostLibraryConfiguration),
                typeof(GetCliProfile), typeof(ValidateCliProfile), typeof(SaveCliProfile), typeof(GetPlaybackStrategyProposal),
                typeof(ChangePlaybackStrategy), typeof(GetPluginState), typeof(InstallPluginPackage), typeof(SetPluginTrust),
                typeof(SetPluginEnabled), typeof(RemovePlugin), typeof(RestartPluginWorker), typeof(InvokePluginAction),
                typeof(GetPluginPanel), typeof(SubmitPluginPanel), typeof(GetVisualization), typeof(SelectVisualization),
                typeof(GetShortcutBindings), typeof(ResolveContextualShortcut), typeof(AssignShortcut),
                typeof(ResetShortcuts), typeof(SetGlobalPlaybackShortcuts));
            var initialized = await dispatcher.SendAsync(new InitializePlayback(playback.SessionId), Attempt(), token).ConfigureAwait(false);
            if (initialized is Outcome<PlaybackState>.Cancelled)
                return new Outcome<DesktopStartupState>.Cancelled();
            if (presentationScheduler is { } scheduler)
            {
                var initial = (initialized as Outcome<PlaybackState>.Success)?.Value;
                presentation = await DesktopPresentation.StartAsync(new(dispatcher, scheduler, origin), shell, arrivals, initial, library, origin, token,
                    session).ConfigureAwait(false);
                arrivals = null;
            }
            var state = new DesktopStartupState(startup, dispatcher, playback.SessionId, initialized, presentation, pluginPackages);
            var pump = audio is IPlaybackMixTarget ? new DesktopPlaybackPump(dispatcher, playback.SessionId, origin, presentation?.Player) : null;
            var replica = host is null ? null : new DesktopReplicaRefresh(dispatcher, library, origin, presentation?.Replica, ReplayPlaylistsAsync);
            var shortcutDispatch = shortcutPlatform is null ? null :
                new DesktopSystemShortcutDispatch(shortcutPlatform, shortcuts, dispatcher, playback.SessionId, origin,
                    presentation?.Player, presentation?.Shortcuts);
            active = new(state, dispatcher, playback.SessionId, store, diagnostics, audio, session, pump, replica, plugins, shortcuts, shortcutDispatch);
            pump?.Start();
            replica?.Start();
            shortcutDispatch?.Start();
            plugins.Start(dispatcher, origin, playback.SessionId, visualization, presentation?.Extensions);
            return new Outcome<DesktopStartupState>.Success(state);

            // 2026-11-25: Resume named and queue outboxes through their existing owners for FR-RECORDS-001#ac-offline.
            async Task ReplayPlaylistsAsync(RequestContext request, CancellationToken ct)
            {
                await playlists.ReplayPendingSavesAsync(request, ct).ConfigureAwait(false);
                var recovered = await session.ReplayQueuePlaylistSavesAsync(playback.SessionId, request, ct).ConfigureAwait(false);
                if (state.Presentation is { } models && recovered is Outcome<PlaybackState>.Success { Value: var next })
                    await models.Player.ReceiveAsync(next).ConfigureAwait(false);
            }
        }
        finally
        {
            if (active is null)
            {
                try { arrivals?.Dispose(); }
                finally { await ReleaseAsync(store, diagnostics, audio, session, presentation, plugins, shortcuts).ConfigureAwait(false); }
            }
        }
    }

    private async Task<Outcome<PlaybackState?>> StopCoreAsync(CancellationToken token, bool releaseOnFailure)
    {
        if (active is not { } current)
            return new Outcome<PlaybackState?>.Success(null);
        var release = releaseOnFailure;
        try
        {
            await Task.WhenAll(current.Pump?.StopAsync() ?? Task.CompletedTask, current.Replica?.StopAsync() ?? Task.CompletedTask,
                current.ShortcutDispatch?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            var saved = await current.Dispatcher.SendAsync(new SavePlaybackForRestart(current.SessionId), Attempt(), token).ConfigureAwait(false);
            if (!releaseOnFailure && saved is not Outcome<PlaybackState>.Success)
                return ForwardFailure<PlaybackState, PlaybackState?>(saved);
            // 2026-11-25: Stop an external engine after recording restart eligibility for FR-CLI-001#ac-switch.
            var stopped = await current.Playback.StopExternalPlaybackAsync(current.SessionId, releaseOnFailure ? CancellationToken.None : token)
                .ConfigureAwait(false);
            if (saved is not Outcome<PlaybackState>.Success success)
                return ForwardFailure<PlaybackState, PlaybackState?>(saved);
            if (stopped is not Outcome<PlaybackState>.Success)
                return ForwardFailure<PlaybackState, PlaybackState?>(stopped);
            release = true;
            return new Outcome<PlaybackState?>.Success(success.Value);
        }
        finally
        {
            if (release)
            {
                active = null;
                await ReleaseAsync(current.Store, current.Diagnostics, current.Audio, current.Playback, current.State.Presentation, current.Plugins,
                    current.Shortcuts)
                    .ConfigureAwait(false);
            }
            else
            {
                current.Pump?.Start();
                current.Replica?.Start();
                current.ShortcutDispatch?.Start();
            }
        }
    }

    private CommandContext Attempt() => new(new(Guid.NewGuid(), origin), Guid.NewGuid());

    /// <summary>Composes only the active host's opt-in platform without starting a registration.</summary>
    /// <remarks>2026-11-25: Select user32 or the Linux XDG portal for FR-SETTINGS-001#ac-shortcuts.</remarks>
    private static ISystemPlaybackShortcutPlatform? CreateShortcutPlatform()
    {
        if (OperatingSystem.IsWindows())
            return new Win32ShortcutPlatform(new Win32ShortcutApi());
        return OperatingSystem.IsLinux() ? new XdgShortcutPlatform(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")) : null;
    }

    private static async ValueTask ReleaseAsync(LiteDbDesktopPlaylistStore store, DiagnosticsLoggerProvider? diagnostics,
        IDesktopPlaybackOutput? audio, PlaybackSessionHandlers? playback, DesktopPresentation? presentation, DesktopPluginMonitor? plugins,
        ShortcutHandlers? shortcuts)
    {
        try
        {
            try
            {
                await Task.WhenAll(plugins?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                    shortcuts?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
            }
            finally
            {
                if (presentation is not null)
                    await presentation.StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try { playback?.CloseExternalPlayback(); }
            finally
            {
                try { audio?.Dispose(); }
                finally
                {
                    try { diagnostics?.Dispose(); }
                    finally { store.Dispose(); }
                }
            }
        }
    }

    private static Outcome<TTo> ForwardFailure<TFrom, TTo>(Outcome<TFrom> result) => result switch
    {
        Outcome<TFrom>.Invalid invalid => new Outcome<TTo>.Invalid(invalid.Issues),
        Outcome<TFrom>.Conflict conflict => new Outcome<TTo>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<TFrom>.Cancelled => new Outcome<TTo>.Cancelled(),
        Outcome<TFrom>.DependencyFailure failure => new Outcome<TTo>.DependencyFailure(failure.Code, failure.Retryable),
        _ => throw new InvalidOperationException("Forward only a failed result.")
    };

    private sealed record ActiveDesktop(DesktopStartupState State, IRequestDispatcher Dispatcher, Guid SessionId,
        LiteDbDesktopPlaylistStore Store, DiagnosticsLoggerProvider Diagnostics, IDesktopPlaybackOutput Audio, PlaybackSessionHandlers Playback, DesktopPlaybackPump? Pump,
        DesktopReplicaRefresh? Replica, DesktopPluginMonitor Plugins, ShortcutHandlers Shortcuts, DesktopSystemShortcutDispatch? ShortcutDispatch);
}
