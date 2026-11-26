using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Desktop.Presentation;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Refreshes the registered workspace's durable catalog independently of windows and peer availability.</summary>
/// <remarks>2026-11-25: Fetch approved host changes and retry unconfirmed acknowledgements for FR-RECORDS-001#ac-notify.</remarks>
/// <remarks>2026-11-25: Resume durable playlist publication after a verified refresh for FR-RECORDS-001#ac-offline.</remarks>
internal sealed class DesktopReplicaRefresh(IRequestDispatcher dispatcher, Guid library, Guid origin, ReplicaViewModel? projection,
    Func<RequestContext, CancellationToken, Task> replayPlaylists)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? lifetime;
    private Task? running;

    /// <summary>Starts a single automatic refresh loop for the current registration.</summary>
    internal void Start()
    {
        if (lifetime is not null)
            return;
        lifetime = new CancellationTokenSource();
        running = RunAsync(lifetime.Token);
    }

    /// <summary>Joins the loop before releasing the registered desktop's local store.</summary>
    internal async Task StopAsync()
    {
        if (lifetime is not { } active)
            return;
        try
        {
            await active.CancelAsync().ConfigureAwait(false);
            await running!.ConfigureAwait(false);
        }
        finally
        {
            active.Dispose();
            lifetime = null;
            running = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var delay = Interval;
        ReplicationState? presented = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var context = new CommandContext(new(Guid.NewGuid(), origin), Guid.NewGuid());
                var refreshed = await dispatcher.SendAsync(new RefreshReplication(library, origin), context, token).ConfigureAwait(false);
                if (refreshed is Outcome<ReplicationState>.Success { Value: var updated })
                {
                    delay = Interval;
                    if (!updated.SnapshotRequired)
                        await replayPlaylists(context.Request, token).ConfigureAwait(false);
                    if (projection is not null && !SameState(updated, presented))
                    {
                        await projection.ReceiveAsync(updated).ConfigureAwait(false);
                        presented = updated;
                    }
                }
                else
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumRetryDelay.Ticks));
                }
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static bool SameState(ReplicationState incoming, ReplicationState? previous) => previous is not null &&
        incoming with { Conflicts = previous.Conflicts } == previous && incoming.Conflicts.SequenceEqual(previous.Conflicts);
}
