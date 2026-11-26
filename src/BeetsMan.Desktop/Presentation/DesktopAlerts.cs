using System.Threading.Channels;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies the three enabled-by-preference desktop alerts.</summary>
/// <remarks>2026-11-25: Represent the STB07 alerts for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
public enum DesktopAlertKind { SyncCompleted, PeerNeedsAttention, PlaybackError }

/// <summary>Reports the transfer, share, or track associated with one alert.</summary>
public sealed record DesktopAlert(DesktopAlertKind Kind, Guid SubjectId, string? Code = null);

/// <summary>Delivers current workspace alerts through the scheduler using the effective preference for each event.</summary>
/// <remarks>2026-11-25: Resolve STB07 event preferences at delivery for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
public sealed class DesktopAlerts
{
    private readonly Channel<DesktopAlert> pending = Channel.CreateBounded<DesktopAlert>(
        new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private Dictionary<Guid, MediaSharePhase>? sharePhases;
    private Guid? publishedTransfer;

    /// <summary>Occurs on the presentation scheduler when a live alert is enabled.</summary>
    public event Action<DesktopAlert>? Raised;

    internal void FollowPublishedTransfer(Guid? transferId) => publishedTransfer = transferId;

    internal void ObserveTransfer(MediaTransferState? state)
    {
        if (state is null || state.TransferId != publishedTransfer)
            return;
        switch (state.Phase)
        {
            case TransferPhase.Complete:
                publishedTransfer = null;
                Enqueue(new(DesktopAlertKind.SyncCompleted, state.TransferId));
                break;
            case TransferPhase.Failed:
                publishedTransfer = null;
                Enqueue(new(DesktopAlertKind.PeerNeedsAttention, state.ShareId, state.FailureCode));
                break;
        }
    }

    internal void ObserveShares(MediaSyncState? state)
    {
        if (state is null)
            return;
        var previous = sharePhases;
        sharePhases = new(state.Shares.Length);
        foreach (var share in state.Shares)
        {
            var phase = share.Progress?.Phase ?? MediaSharePhase.Unknown;
            sharePhases[share.ShareId] = phase;
            if (phase == MediaSharePhase.Failed && previous?.TryGetValue(share.ShareId, out var prior) == true &&
                prior is not (MediaSharePhase.Failed or MediaSharePhase.Unknown))
                Enqueue(new(DesktopAlertKind.PeerNeedsAttention, share.ShareId, share.Progress?.FailureCode));
        }
    }

    internal void PlaybackFailed(Guid trackId, string code) => Enqueue(new(DesktopAlertKind.PlaybackError, trackId, code));

    private void Enqueue(DesktopAlert alert) => pending.Writer.TryWrite(alert);

    /// <summary>Resolves flags using the existing preference route and leaves transport or host workers independent of delivery.</summary>
    internal async Task RunAsync(PresentationServices services, CancellationToken token)
    {
        try
        {
            await foreach (var alert in pending.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var key = alert.Kind switch
                {
                    DesktopAlertKind.SyncCompleted => NotificationPreferences.SyncCompleted,
                    DesktopAlertKind.PeerNeedsAttention => NotificationPreferences.PeerNeedsAttention,
                    _ => NotificationPreferences.PlaybackErrors
                };
                var result = await services.Dispatcher.QueryAsync(new GetPreference(key), services.NewRequest(), token).ConfigureAwait(false);
                if (result is Outcome<EffectivePreference>.Success { Value: { Value: PreferenceValue.Flag { Value: true } } enabled } &&
                    enabled.Key == key)
                    await services.Scheduler.PostAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                            Raised?.Invoke(alert);
                    }, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { pending.Writer.TryComplete(); }
    }
}
