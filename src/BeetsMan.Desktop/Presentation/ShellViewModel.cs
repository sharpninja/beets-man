using BeetsMan.Desktop.Application.PresentationState;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns window navigation, modal ownership, and the current playback presentation.</summary>
/// <remarks>2026-11-24: Bind FR-PLAYER-001#ac-modes and FR-FILESYNC-001#ac-arrival to scoped CQRS commands.</remarks>
public sealed partial class ShellViewModel(PresentationServices services, Guid windowId) : PresentationViewModel(services, windowId)
{
    private ShellState? state;

    /// <summary>Gets the last authoritative shell snapshot.</summary>
    public ShellState? State => state;

    /// <summary>Gets the owning window identity.</summary>
    public Guid WindowId { get; } = windowId;

    [RelayCommand(CanExecute = nameof(CanChangeShell))]
    private Task NavigateAsync(WorkspaceRoute route, CancellationToken token) =>
        WriteAsync(new NavigateWorkspace(WindowId, route), Apply, token, Accepts);

    [RelayCommand(CanExecute = nameof(CanChangeShell))]
    private Task OpenPresentationChooserAsync(CancellationToken token) =>
        WriteAsync(new OpenOverlay(WindowId, WindowId, new OverlayIntent.ChoosePresentation()), Apply, token, Accepts);

    [RelayCommand(CanExecute = nameof(CanChangeShell))]
    private Task ChangePresentationAsync(ModeSelection selection, CancellationToken token) =>
        WriteAsync(new ChangePresentation(WindowId, selection), Apply, token, Accepts);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CancelDialogAsync(Guid overlayId, CancellationToken token) => CloseAsync(overlayId, OverlayCloseReason.Cancel, token);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CloseDialogAsync(Guid overlayId, CancellationToken token) => CloseAsync(overlayId, OverlayCloseReason.Close, token);

    /// <summary>Applies a live, registered Android arrival supplied by the application event adapter.</summary>
    public async Task OnAndroidPeerArrivalAsync(Guid peerId, long generation, CancellationToken token = default)
    {
        Task attempt = Task.CompletedTask;
        var scope = Scope;
        await PostAsync(scope, () => attempt = DispatchEventAsync(new ApplyAndroidPeerArrival(WindowId, peerId, generation), Apply, Accepts, token));
        await attempt;
    }

    internal Task OpenOverlayAsync(Guid ownerId, OverlayIntent intent, CancellationToken token) =>
        WriteAsync(new OpenOverlay(WindowId, ownerId, intent), Apply, token, Accepts);

    internal Task CloseAsync(Guid overlayId, OverlayCloseReason reason, CancellationToken token) => CanClose(overlayId)
        ? WriteAsync(new CloseOverlay(WindowId, overlayId, reason), Apply, token, Accepts)
        : Task.CompletedTask;

    internal bool CanClose(Guid overlayId) => CanWrite && state is { } snapshot && !snapshot.Overlays.IsDefaultOrEmpty &&
        snapshot.Overlays[^1].OverlayId == overlayId;

    protected override void OnWriteAvailabilityChanged()
    {
        NavigateCommand.NotifyCanExecuteChanged();
        OpenPresentationChooserCommand.NotifyCanExecuteChanged();
        ChangePresentationCommand.NotifyCanExecuteChanged();
        CancelDialogCommand.NotifyCanExecuteChanged();
        CloseDialogCommand.NotifyCanExecuteChanged();
    }

    private bool CanChangeShell() => CanWrite;
    private bool Accepts(ShellState incoming) => incoming.WindowId == WindowId && (state is null || incoming.Revision >= state.Revision);

    private void Apply(ShellState incoming)
    {
        SetProperty(ref state, incoming, nameof(State));
        OnWriteAvailabilityChanged();
    }
}

/// <summary>Owns feedback and the typed retry callback for an application action.</summary>
/// <remarks>2026-11-24: Preserve the typed failure and original operation for FR-DISPATCH-001#ac-routing.</remarks>
public sealed partial class ActionErrorViewModel(PresentationServices services, ShellViewModel shell)
    : PresentationViewModel(services, Guid.NewGuid())
{
    private Guid overlayId;
    private PresentationFailure? error;

    /// <summary>Gets the error shown by this scope.</summary>
    public PresentationFailure? Error => error;

    /// <summary>Attaches feedback to an existing shell-owned overlay.</summary>
    public void Show(Guid owningOverlayId, PresentationFailure current)
    {
        overlayId = owningOverlayId;
        SetProperty(ref error, current, nameof(Error));
        RetryCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private Task DismissAsync(CancellationToken token) => shell.CloseAsync(overlayId, OverlayCloseReason.Dismiss, token);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync(CancellationToken token) => error?.Retry?.Invoke(token) ?? Task.CompletedTask;

    private bool CanDismiss() => IsActive && shell.CanClose(overlayId);
    private bool CanRetry() => IsActive && error?.CanRetry == true;
}
