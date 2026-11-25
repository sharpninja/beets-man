using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.PresentationState;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures an explicit pairing proposal and its expected media revision.</summary>
public sealed record PairPeerInput(Guid PeerId, ShareSelection Share, long Revision);

/// <summary>Owns device pairing, PC mirror controls and transfer recovery.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-mirror, ac-arrival and ac-safety without blocking navigation.</remarks>
public sealed partial class MediaSyncViewModel(PresentationServices services, Guid ownerId, ShellViewModel shell)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private MediaSyncState? state;
    [ObservableProperty] private Guid? selectedPeerId;
    [ObservableProperty] private MediaTransferState? transfer;

    private ResumeMediaTransfer? retryIntent;
    private Guid retryOperation;

    [RelayCommand]
    private Task LoadAsync(Guid? routePeerId, CancellationToken token) => ReadAsync(new GetMediaSyncState(), "media", result =>
    {
        State = result;
        if (routePeerId is { } peer && result.Peers.Any(item => item.PeerId == peer))
            SelectedPeerId = peer;
    }, token, Accepts);

    /// <summary>Loads the usual media projection for the peer selected by the shell's accepted arrival.</summary>
    /// <remarks>2026-11-24: Bind the STB21 destination query for FR-FILESYNC-001#ac-arrival.</remarks>
    internal async Task LoadArrivedPeerAsync(ShellViewModel shell, Guid peer, CancellationToken token)
    {
        Task read = Task.CompletedTask;
        await PostAsync(Scope, () =>
        {
            if (shell.IsActive && shell.State?.Route is { Workspace: Workspace.Sync, PeerId: var selected } && selected == peer)
                read = LoadAsync(peer, token);
        }).ConfigureAwait(false);
        await read.ConfigureAwait(false);
    }

    [RelayCommand]
    private Task OpenMirrorsAsync(CancellationToken token) => LoadAsync(null, token);

    [RelayCommand]
    private Task BeginPairingAsync(CancellationToken token) => shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.PairMediaPeer(), token);

    [RelayCommand]
    private Task ShareFilesAsync(PairPeerInput input, CancellationToken token) =>
        WriteAsync(new PairMediaPeer(input.PeerId, input.Share, input.Revision), incoming =>
        {
            State = incoming;
            if (incoming.Peers.Any(peer => peer.PeerId == input.PeerId))
                SelectedPeerId = input.PeerId;
        }, token, Accepts);

    [RelayCommand]
    private Task RescanAsync(Guid shareId, CancellationToken token) =>
        WriteAsync(new RescanMediaShare(shareId), incoming => State = incoming, token, Accepts);

    [RelayCommand]
    private Task PauseMirrorAsync(MediaShare share, CancellationToken token) => SetMirrorAsync(share, true, token);

    [RelayCommand]
    private Task ResumeMirrorAsync(MediaShare share, CancellationToken token) => SetMirrorAsync(share, false, token);

    [RelayCommand]
    private Task InspectTransferAsync(Guid transferId, CancellationToken token) => ReadAsync(new GetMediaTransfer(transferId), "transfer",
        incoming => Transfer = incoming, token, incoming => incoming.TransferId == transferId && AcceptsTransfer(incoming));

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryTransferAsync(CancellationToken token)
    {
        if (!CanRetry() || Transfer is not { } current)
            return Task.CompletedTask;
        var command = new ResumeMediaTransfer(current.TransferId, current.Revision);
        // Keep the operation identity when the host commits but the acknowledgement fails to arrive.
        if (retryIntent != command)
        {
            retryIntent = command;
            retryOperation = Guid.NewGuid();
        }
        return WriteAsync(command, incoming => Transfer = incoming, token,
            incoming => incoming.TransferId == current.TransferId && Transfer?.TransferId == current.TransferId && AcceptsTransfer(incoming),
            operationId: retryOperation);
    }

    /// <summary>Applies background progress only for the selected transfer.</summary>
    public Task ReceiveTransferAsync(MediaTransferState incoming) => PostAsync(Scope, () =>
    {
        if (Transfer?.TransferId == incoming.TransferId && AcceptsTransfer(incoming))
            Transfer = incoming;
    });

    protected override void OnWriteAvailabilityChanged() => RetryTransferCommand.NotifyCanExecuteChanged();
    partial void OnTransferChanged(MediaTransferState? value) => RetryTransferCommand.NotifyCanExecuteChanged();

    private bool CanRetry() => CanWrite && Transfer?.Retryable == true;
    private bool Accepts(MediaSyncState incoming) => incoming.Revision >= (State?.Revision ?? 0);
    private bool AcceptsTransfer(MediaTransferState incoming) => Transfer?.TransferId != incoming.TransferId || incoming.Revision >= Transfer.Revision;

    private Task SetMirrorAsync(MediaShare share, bool paused, CancellationToken token) => State is { } current &&
        current.Shares.Contains(share) && current.Peers.Any(peer => peer.PeerId == share.PeerId && peer.Kind == MediaPeerKind.PcMirror)
            ? WriteAsync(new SetMirrorPaused(share.ShareId, paused, share.Revision), incoming => State = incoming, token, Accepts)
            : Task.CompletedTask;
}
