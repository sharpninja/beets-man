using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.PresentationState;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures an explicit pairing proposal and its expected media revision.</summary>
public sealed record PairPeerInput(Guid PeerId, ShareSelection Share, long Revision);

/// <summary>Owns device pairing, PC mirror controls and transfer recovery.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-mirror, ac-arrival and ac-safety without blocking navigation.</remarks>
/// <remarks>2026-11-25: Load the scoped export for the selected Android peer for FR-FILESYNC-001#ac-arrival and ac-playlist-drop.</remarks>
/// <remarks>2026-11-25: Follow the selected publication by transfer ID through FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class MediaSyncViewModel(PresentationServices services, Guid ownerId, ShellViewModel shell,
    DeviceExportViewModel? deviceExport = null)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private MediaSyncState? state;
    [ObservableProperty] private Guid? selectedPeerId;
    [ObservableProperty] private MediaTransferState? transfer;

    private GetMediaTransfer? trackedTransfer;
    private CancellationTokenSource? transferLifetime;
    private ResumeMediaTransfer? retryIntent;
    private Guid retryOperation;

    [RelayCommand]
    private Task LoadAsync(Guid? routePeerId, CancellationToken token) => LoadPeerAsync(routePeerId, token);

    /// <summary>Confirms the scoped query and optional device projection for the current selection.</summary>
    /// <remarks>2026-11-26: Acknowledge only applied projections for FR-FILESYNC-001#ac-arrival.</remarks>
    private async Task<bool> LoadPeerAsync(Guid? routePeerId, CancellationToken token, Func<bool>? isArrivalRoute = null)
    {
        var loadDevice = Task.FromResult(false);
        var applied = await ReadAsync(new GetMediaSyncState(), "media", result =>
        {
            State = result;
            var peer = routePeerId ?? SelectedPeerId;
            var selected = result.Peers.FirstOrDefault(item => item.PeerId == peer);
            if (isArrivalRoute is null || selected is { Kind: MediaPeerKind.Android, Connected: true })
                loadDevice = SelectAsync(selected, token);
        }, token, result => Accepts(result) && (isArrivalRoute?.Invoke() ?? true)).ConfigureAwait(false);
        return applied && await loadDevice.ConfigureAwait(false);
    }

    /// <summary>Loads the usual media projection for the peer selected by the shell's accepted arrival.</summary>
    /// <remarks>2026-11-24: Bind the STB21 destination query for FR-FILESYNC-001#ac-arrival.</remarks>
    /// <remarks>2026-11-26: Retry an incomplete projection and respect a newer route for FR-FILESYNC-001#ac-arrival.</remarks>
    internal async Task<bool> LoadArrivedPeerAsync(ShellViewModel shell, Guid peer, CancellationToken token)
    {
        var read = Task.FromResult(false);
        await PostAsync(Scope, () =>
        {
            if (shell.IsActive)
                read = IsArrivalRoute() ? LoadPeerAsync(peer, token, IsArrivalRoute) : Task.FromResult(true);
        }).ConfigureAwait(false);
        return await read.ConfigureAwait(false);

        bool IsArrivalRoute() => shell.IsActive && shell.State?.Route is { Workspace: Workspace.Sync, PeerId: var selected } && selected == peer;
    }

    [RelayCommand]
    private Task OpenMirrorsAsync(CancellationToken token) => LoadAsync(null, token);

    [RelayCommand]
    private Task BeginPairingAsync(CancellationToken token) => shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.PairMediaPeer(), token);

    [RelayCommand]
    private async Task ShareFilesAsync(PairPeerInput input, CancellationToken token)
    {
        var loadDevice = Task.CompletedTask;
        await WriteAsync(new PairMediaPeer(input.PeerId, input.Share, input.Revision), incoming =>
        {
            State = incoming;
            loadDevice = SelectAsync(incoming.Peers.FirstOrDefault(peer => peer.PeerId == input.PeerId), token);
        }, token, Accepts).ConfigureAwait(false);
        await loadDevice.ConfigureAwait(false);
    }

    /// <summary>Uses the accepted directory entry to scope Android export state.</summary>
    /// <remarks>2026-11-26: Include Android device readiness in FR-FILESYNC-001#ac-arrival acknowledgment.</remarks>
    private Task<bool> SelectAsync(MediaPeer? peer, CancellationToken token)
    {
        if (SelectedPeerId != peer?.PeerId)
            TrackTransfer(null);
        SelectedPeerId = peer?.PeerId;
        return deviceExport?.SelectDeviceAsync(peer is { Kind: MediaPeerKind.Android } ? peer.PeerId : null, token) ?? Task.FromResult(true);
    }

    [RelayCommand]
    private Task RescanAsync(Guid shareId, CancellationToken token) =>
        WriteAsync(new RescanMediaShare(shareId), incoming => State = incoming, token, Accepts);

    [RelayCommand]
    private Task PauseMirrorAsync(MediaShare share, CancellationToken token) => SetMirrorAsync(share, true, token);

    [RelayCommand]
    private Task ResumeMirrorAsync(MediaShare share, CancellationToken token) => SetMirrorAsync(share, false, token);

    [RelayCommand]
    private Task InspectTransferAsync(Guid transferId, CancellationToken token)
    {
        if (!IsActive || transferId == Guid.Empty)
            return Task.CompletedTask;
        TrackTransfer(transferId);
        return ReadTransferAsync(trackedTransfer!, transferLifetime!.Token, token, background: false);
    }

    /// <summary>Follows the newly selected transfer without carrying progress or retry state over from the prior selection.</summary>
    /// <remarks>2026-11-25: Reset the selected transfer on peer changes for FR-FILESYNC-001#ac-safety.</remarks>
    internal void TrackTransfer(Guid? transferId)
    {
        if (transferId == Guid.Empty || trackedTransfer?.TransferId == transferId)
            return;
        ReleaseTransferRead();
        transferLifetime = transferId is null ? null : new CancellationTokenSource();
        Volatile.Write(ref trackedTransfer, transferId is { } id ? new(id) : null);
        retryIntent = null;
        Transfer = null;
    }

    /// <summary>Refreshes the current nonterminal transfer without blocking the workspace or replacing foreground feedback.</summary>
    /// <remarks>2026-11-25: Read current progress through CQRS for FR-FILESYNC-001#ac-safety.</remarks>
    internal async Task RefreshTransferAsync(CancellationToken token)
    {
        if (Volatile.Read(ref trackedTransfer) is null || Transfer?.Phase is TransferPhase.Complete or TransferPhase.Failed)
            return;
        var read = Task.CompletedTask;
        await PostAsync(Scope, () =>
        {
            if (!token.IsCancellationRequested && trackedTransfer is { } request &&
                Transfer?.Phase is not (TransferPhase.Complete or TransferPhase.Failed) &&
                !InspectTransferCommand.IsRunning && !RetryTransferCommand.IsRunning)
                read = ReadTransferAsync(request, transferLifetime!.Token, token, background: true);
        }).ConfigureAwait(false);
        await read.ConfigureAwait(false);
    }

    /// <summary>Applies only a current result for the selected transfer and preserves feedback during background reads.</summary>
    private async Task ReadTransferAsync(GetMediaTransfer request, CancellationToken selection, CancellationToken token, bool background)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(selection, token);
        await ReadAsync(request, background ? "transfer-refresh" : "transfer", incoming => Transfer = incoming, linked.Token,
            incoming => ReferenceEquals(trackedTransfer, request) && AcceptsTransfer(incoming), preserveFailure: background).ConfigureAwait(false);
    }

    /// <summary>Cancels outstanding status reads when the selected transfer or workspace is retired.</summary>
    private void ReleaseTransferRead()
    {
        transferLifetime?.Cancel();
        transferLifetime?.Dispose();
        transferLifetime = null;
    }

    protected override void OnDisposed() => ReleaseTransferRead();

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryTransferAsync(CancellationToken token)
    {
        if (!CanRetry() || Transfer is not { } current)
            return Task.CompletedTask;
        var selection = trackedTransfer;
        var command = new ResumeMediaTransfer(current.TransferId, current.Revision);
        // Keep the operation identity when the host commits but the acknowledgement fails to arrive.
        if (retryIntent != command)
        {
            retryIntent = command;
            retryOperation = Guid.NewGuid();
        }
        return WriteAsync(command, incoming => Transfer = incoming, token,
            incoming => ReferenceEquals(trackedTransfer, selection) && AcceptsTransfer(incoming),
            operationId: retryOperation);
    }

    /// <summary>Applies background progress only for the selected transfer.</summary>
    public Task ReceiveTransferAsync(MediaTransferState incoming) => PostAsync(Scope, () =>
    {
        if (AcceptsTransfer(incoming))
            Transfer = incoming;
    });

    protected override void OnWriteAvailabilityChanged() => RetryTransferCommand.NotifyCanExecuteChanged();
    partial void OnTransferChanged(MediaTransferState? value) => RetryTransferCommand.NotifyCanExecuteChanged();

    private bool CanRetry() => CanWrite && Transfer?.Retryable == true;
    private bool Accepts(MediaSyncState incoming) => incoming.Revision >= (State?.Revision ?? 0);
    private bool AcceptsTransfer(MediaTransferState incoming) => incoming.TransferId == trackedTransfer?.TransferId &&
        incoming.Revision >= (Transfer?.Revision ?? 0);

    private Task SetMirrorAsync(MediaShare share, bool paused, CancellationToken token) => State is { } current &&
        current.Shares.Contains(share) && current.Peers.Any(peer => peer.PeerId == share.PeerId && peer.Kind == MediaPeerKind.PcMirror)
            ? WriteAsync(new SetMirrorPaused(share.ShareId, paused, share.Revision), incoming => State = incoming, token, Accepts)
            : Task.CompletedTask;
}
