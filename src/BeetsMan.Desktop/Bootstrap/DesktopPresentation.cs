using System.ComponentModel;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Provides the active workspace's shell and Sync ViewModels and owns their live arrival subscription.</summary>
/// <remarks>2026-11-25: Start and stop one authenticated Android feed with the projection lifetime for FR-FILESYNC-001#ac-arrival.</remarks>
/// <remarks>2026-11-25: Share the authorized playback clock and automatic fade results for FR-PLAYER-001#ac-fade.</remarks>
/// <remarks>2026-11-25: Activate the profile and strategy ViewModel for FR-CLI-001#ac-profiles and ac-switch.</remarks>
/// <remarks>2026-11-25: Compose the diagnostics ViewModel and log-folder handoff for FR-CONFIG-001#ac-logs.</remarks>
/// <remarks>2026-11-25: Compose the host library setup and maintenance ViewModel for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Share the host configuration owner with Settings for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Compose the Library, shared queue, playlist editor and local batch lifecycle for FR-LIBRARY-001#ac-browse, FR-PLAYER-001#ac-queue-autosave and FR-LIBRARY-002#ac-batch-review.</remarks>
/// <remarks>2026-11-25: Compose the selected Android plan and export review for FR-FILESYNC-001#ac-arrival, ac-layouts and ac-playlist-drop.</remarks>
/// <remarks>2026-11-25: Follow a published transfer until completion or workspace shutdown for FR-FILESYNC-001#ac-safety.</remarks>
/// <remarks>2026-11-25: Subscribe to live Sync and engine outcomes for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
public sealed class DesktopPresentation
{
    private readonly AndroidArrivalMonitor? arrivals;
    private readonly CancellationTokenSource stop = new();
    private readonly PlaybackSessionHandlers? playbackSession;
    private readonly PresentationViewModel[] viewModels;
    private QueueSnapshot? projectedQueue;
    private Task? polling;
    private Task? transferPolling;
    private Task? alertDelivery;

    private DesktopPresentation(PresentationServices services, ShellState shell, AndroidArrivalMonitor? arrivals, Guid library, Guid origin,
        PlaybackSessionHandlers? playbackSession)
    {
        Services = services;
        Shell = new(services, shell.WindowId, shell);
        SyncList = new(services, Guid.NewGuid(), new(Guid.Empty, string.Empty, 0));
        DeviceExport = new(services, Guid.NewGuid(), SyncList);
        Sync = new(services, Guid.NewGuid(), Shell, DeviceExport);
        Alerts = new();
        Playback = new(shell.PlaybackSessionId);
        Player = new(services, Playback);
        CliPlayback = new(services, Guid.NewGuid(), Playback);
        Extensions = new(services, Guid.NewGuid(), Playback);
        Shortcuts = new(services, Guid.NewGuid());
        HostLibrary = new(services, Guid.NewGuid(), library);
        Settings = new(services, Guid.NewGuid(), HostLibrary);
        Diagnostics = new(services, Guid.NewGuid());
        Replica = new(services, Guid.NewGuid(), library, origin, Shell);
        Library = new(services, Guid.NewGuid(), Playback);
        Queue = new(services, Playback);
        PlaylistDraft = new(services, Guid.NewGuid(), Playback);
        LibraryBatch = new(services, Guid.NewGuid(), library);
        TagEdit = new(services, Guid.NewGuid());
        ActionError = new(services, Shell);
        viewModels = [Shell, SyncList, DeviceExport, Sync, Player, CliPlayback, Extensions, Shortcuts, HostLibrary, Settings, Diagnostics, Replica,
            Library, Queue, PlaylistDraft, LibraryBatch, TagEdit, ActionError];
        services.BatchStageAccepted += LibraryBatch.ReceiveStage;
        services.BatchModeChanged += PlaylistDraft.ReceiveBatchMode;
        Playback.PropertyChanged += PresentQueueState;
        DeviceExport.PropertyChanged += TrackPublication;
        Sync.PropertyChanged += ObserveSyncAlert;
        this.playbackSession = playbackSession;
        if (playbackSession is not null)
            playbackSession.EngineFailed += ObserveEngineAlert;
        this.arrivals = arrivals;
    }

    /// <summary>Gets the shared dispatcher and scheduler for this workspace's ViewModels.</summary>
    public PresentationServices Services { get; }

    /// <summary>Gets the owner of navigation, presentation mode, and modal state.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>Gets the Sync ViewModel that receives the shell's approved Android selection.</summary>
    public MediaSyncViewModel Sync { get; }

    /// <summary>Gets the staged list for the selected Android device and player profile.</summary>
    public SyncListViewModel SyncList { get; }

    /// <summary>Gets the profile selection and export review tied to that staged list.</summary>
    public DeviceExportViewModel DeviceExport { get; }

    /// <summary>Gets the enabled live alerts delivered on this workspace's scheduler.</summary>
    public DesktopAlerts Alerts { get; }

    /// <summary>Gets the shared playback snapshot for the active session.</summary>
    public PlaybackPresentation Playback { get; }

    /// <summary>Gets the transport and mixer commands bound to the shared playback session.</summary>
    public PlaybackViewModel Player { get; }

    /// <summary>Gets the profile editor and engine switch commands for the shared session.</summary>
    public CliPlaybackViewModel CliPlayback { get; }

    /// <summary>Gets plugin management and live worker diagnostics for the desktop.</summary>
    public ExtensionsViewModel Extensions { get; }

    /// <summary>Gets the workspace's contextual and opt-in system shortcut commands.</summary>
    /// <remarks>2026-11-25: Activate the editor for FR-SETTINGS-001#ac-shortcuts.</remarks>
    public ShortcutsViewModel Shortcuts { get; }

    /// <summary>Gets the desktop's effective preferences and reviewed reset commands.</summary>
    /// <remarks>2026-11-25: Compose the STB07 settings routes for FR-SETTINGS-001.</remarks>
    public SettingsViewModel Settings { get; }

    /// <summary>Gets the redacted diagnostics and platform log-folder actions for the active desktop.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>Gets the active desktop's host library setup and maintenance commands.</summary>
    public HostLibraryViewModel HostLibrary { get; }

    /// <summary>Gets the registered workspace's current catalog checkpoint and recovery actions.</summary>
    public ReplicaViewModel Replica { get; }

    /// <summary>Gets the catalog and selection owner that can enqueue tracks in the active session.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>Gets the editor over the active playback session's queue.</summary>
    public QueueViewModel Queue { get; }

    /// <summary>Gets the playlist editor that can copy the current queue order.</summary>
    public PlaylistDraftViewModel PlaylistDraft { get; }

    /// <summary>Gets the library's active batch toggle and review owner.</summary>
    public LibraryBatchViewModel LibraryBatch { get; }

    /// <summary>Gets the owner of the active track tag draft.</summary>
    public TagEditViewModel TagEdit { get; }

    /// <summary>Gets the action feedback owner tied to the active shell.</summary>
    public ActionErrorViewModel ActionError { get; }

    /// <summary>Activates the ViewModels on the scheduler before starting the registered feed.</summary>
    internal static async Task<DesktopPresentation> StartAsync(PresentationServices services, ShellState shell,
        AndroidArrivalMonitor? arrivals, PlaybackState? playback, Guid library, Guid origin, CancellationToken token,
        PlaybackSessionHandlers? playbackSession = null)
    {
        var presentation = new DesktopPresentation(services, shell, arrivals, library, origin, playbackSession);
        try
        {
            var loadBatch = Task.CompletedTask;
            await services.Scheduler.PostAsync(() =>
            {
                foreach (var model in presentation.viewModels)
                    model.Activate(model.Scope.OwnerId);
                if (playback is not null)
                    presentation.Playback.Apply(playback);
                loadBatch = presentation.LibraryBatch.LoadAsync(token);
            }, token).ConfigureAwait(false);
            await loadBatch.ConfigureAwait(false);
        }
        catch
        {
            presentation.stop.Dispose();
            await presentation.DisposeViewModelsAsync().ConfigureAwait(false);
            throw;
        }
        var lifetime = presentation.stop.Token;
        if (arrivals is not null)
        {
            presentation.polling = arrivals.RunAsync(presentation.Shell, presentation.Sync, lifetime);
            presentation.transferPolling = presentation.PollTransferAsync(lifetime);
        }
        presentation.alertDelivery = presentation.Alerts.RunAsync(services, lifetime);
        return presentation;
    }

    /// <summary>Cancels and joins a pending long poll, then disposes its ViewModels on the owning scheduler.</summary>
    internal async ValueTask StopAsync()
    {
        try
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(polling ?? Task.CompletedTask, transferPolling ?? Task.CompletedTask,
                alertDelivery ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            stop.Dispose();
            arrivals?.Dispose();
            await DisposeViewModelsAsync().ConfigureAwait(false);
        }
    }

    private Task DisposeViewModelsAsync() => Services.Scheduler.PostAsync(() =>
    {
        if (playbackSession is not null)
            playbackSession.EngineFailed -= ObserveEngineAlert;
        Sync.PropertyChanged -= ObserveSyncAlert;
        DeviceExport.PropertyChanged -= TrackPublication;
        Playback.PropertyChanged -= PresentQueueState;
        Services.BatchModeChanged -= PlaylistDraft.ReceiveBatchMode;
        Services.BatchStageAccepted -= LibraryBatch.ReceiveStage;
        foreach (var model in viewModels.Reverse())
            model.Dispose();
    }, CancellationToken.None);

    /// <summary>Starts following the transfer acknowledged by a successful export publication.</summary>
    private void TrackPublication(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DeviceExportViewModel.Publication) && DeviceExport.Publication?.TransferId is { } transfer)
        {
            Alerts.FollowPublishedTransfer(transfer);
            Sync.TrackTransfer(transfer);
        }
    }

    /// <summary>Passes peer and transfer state changes to the alert owner on the scheduler.</summary>
    private void ObserveSyncAlert(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MediaSyncViewModel.State): Alerts.ObserveShares(Sync.State); break;
            case nameof(MediaSyncViewModel.Transfer): Alerts.ObserveTransfer(Sync.Transfer); break;
            case nameof(MediaSyncViewModel.SelectedPeerId): Alerts.FollowPublishedTransfer(null); break;
        }
    }

    /// <summary>Queues the active session's engine failure without blocking the playback command lane.</summary>
    private void ObserveEngineAlert(PlaybackEngineFailure failure)
    {
        if (failure.SessionId == Playback.SessionId)
            Alerts.PlaybackFailed(failure.TrackId, failure.Code);
    }

    /// <summary>Reads selected transfer progress serially until the registered workspace stops.</summary>
    /// <remarks>2026-11-25: Join the progress loop at shutdown for FR-FILESYNC-001#ac-safety.</remarks>
    private async Task PollTransferAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                await Sync.RefreshTransferAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Updates the playlist editor and batch review when the shared queue snapshot changes.</summary>
    /// <remarks>2026-11-25: Project queue availability and auto-save for FR-LIBRARY-001#ac-browse and FR-LIBRARY-002#ac-batch-review.</remarks>
    private void PresentQueueState(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(PlaybackPresentation.State) || Playback.State?.Queue == projectedQueue)
            return;
        projectedQueue = Playback.State?.Queue;
        PlaylistDraft.CreateFromQueueCommand.NotifyCanExecuteChanged();
        if (projectedQueue?.BatchSave is { } stage)
            Services.PresentBatchStage(stage);
    }
}
