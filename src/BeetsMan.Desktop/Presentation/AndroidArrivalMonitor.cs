using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.PresentationState;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Feeds authenticated live Android arrivals through the existing shell and Sync ViewModels.</summary>
/// <remarks>2026-11-24: Preserve STB21 event freshness and modal ownership for FR-FILESYNC-001#ac-arrival.</remarks>
public sealed class AndroidArrivalMonitor(IAndroidArrivalHost host, Guid originInstanceId) : IAndroidConnectionRegistry, IDisposable
{
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly object gate = new();
    private Dictionary<Guid, LiveConnection> live = [];
    private Guid? epoch;
    private long? revision;
    private long generation;

    /// <inheritdoc />
    public bool IsLiveRegisteredConnection(Guid peerId, long generation)
    {
        lock (gate)
            return live.TryGetValue(peerId, out var connection) && connection.Generation == generation;
    }

    /// <summary>Runs the live feed while the desktop session is active; reconnects from a silent baseline.</summary>
    public async Task RunAsync(ShellViewModel shell, MediaSyncViewModel media, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await PollOnceAsync(shell, media, token).ConfigureAwait(false);
                if (result is not Outcome<AndroidArrivalFrame>.Success)
                    await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Reads the next frame and dispatches each fresh confirmed arrival into CQRS.</summary>
    /// <remarks>2026-11-26: Acknowledge the host cursor after routing and projection finish for FR-FILESYNC-001#ac-arrival.</remarks>
    public async Task<Outcome<AndroidArrivalFrame>> PollOnceAsync(ShellViewModel shell, MediaSyncViewModel media, CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await host.ReadArrivalsAsync(epoch, revision, new(Guid.NewGuid(), originInstanceId), token).ConfigureAwait(false);
            if (response is not Outcome<AndroidArrivalFrame>.Success { Value: var frame })
            {
                if (!token.IsCancellationRequested)
                    Reset();
                return response;
            }
            foreach (var (peer, connection) in RefreshLiveConnections(frame))
            {
                if (connection.Progress == ArrivalProgress.Routing)
                {
                    if (!await shell.OnAndroidPeerArrivalAsync(peer, connection.Generation, token).ConfigureAwait(false))
                        return Retry("android-arrival-routing-pending", token);
                    connection.Progress = ArrivalProgress.Projecting;
                }
                if (!await media.LoadArrivedPeerAsync(shell, peer, token).ConfigureAwait(false))
                    return Retry("android-arrival-projection-pending", token);
                connection.Progress = ArrivalProgress.Complete;
            }
            epoch = frame.Epoch;
            revision = frame.Revision;
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<AndroidArrivalFrame>.Cancelled();
        }
        finally { lane.Release(); }
    }

    /// <summary>Reuses the same generation for a fresh connection while the host confirms it is still live.</summary>
    /// <remarks>2026-11-26: Preserve completed routing on a projection retry and drop disconnected or historical arrivals for FR-FILESYNC-001#ac-arrival.</remarks>
    private List<(Guid PeerId, LiveConnection Connection)> RefreshLiveConnections(AndroidArrivalFrame frame)
    {
        lock (gate)
        {
            var ready = new List<(Guid, LiveConnection)>();
            var next = new Dictionary<Guid, LiveConnection>();
            if (!frame.Baseline && epoch == frame.Epoch && revision is { } after && frame.Revision >= after)
            {
                foreach (var peer in frame.Connections.Where(peer => peer.Connected && peer.ArrivedAtRevision is not null)
                    .OrderBy(peer => peer.ArrivedAtRevision))
                {
                    if (live.TryGetValue(peer.PeerId, out var known) && known.ArrivedAt == peer.ArrivedAtRevision)
                    {
                        next.Add(peer.PeerId, known);
                        if (known.Progress != ArrivalProgress.Complete)
                            ready.Add((peer.PeerId, known));
                    }
                    else if (peer.ArrivedAtRevision > after)
                    {
                        var connection = new LiveConnection(peer.ArrivedAtRevision!.Value, checked(++generation));
                        next.Add(peer.PeerId, connection);
                        ready.Add((peer.PeerId, connection));
                    }
                }
            }
            live = next;
            return ready;
        }
    }

    /// <summary>Leaves the cursor unchanged and distinguishes shutdown from a retryable projection failure.</summary>
    /// <remarks>2026-11-26: Preserve arrival identity after a failed attempt for FR-FILESYNC-001#ac-arrival.</remarks>
    private static Outcome<AndroidArrivalFrame> Retry(string code, CancellationToken token) => token.IsCancellationRequested
        ? new Outcome<AndroidArrivalFrame>.Cancelled() : new Outcome<AndroidArrivalFrame>.DependencyFailure(code, true);

    private void Reset()
    {
        lock (gate)
        {
            epoch = null;
            revision = null;
            live.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose() => lane.Dispose();

    /// <summary>Tracks the progress that subsequent frames may observe for this live connection.</summary>
    /// <remarks>2026-11-26: Retain retry progress under the monitor lane for FR-FILESYNC-001#ac-arrival.</remarks>
    private sealed class LiveConnection(long arrivedAt, long generation)
    {
        internal long ArrivedAt { get; } = arrivedAt;
        internal long Generation { get; } = generation;
        internal ArrivalProgress Progress { get; set; }
    }

    /// <summary>Records the next step required before the host cursor can be acknowledged.</summary>
    /// <remarks>2026-11-26: Keep FR-FILESYNC-001#ac-arrival routing and projection separately retryable.</remarks>
    private enum ArrivalProgress { Routing, Projecting, Complete }
}
