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
    public async Task<Outcome<AndroidArrivalFrame>> PollOnceAsync(ShellViewModel shell, MediaSyncViewModel media, CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await host.ReadArrivalsAsync(epoch, revision, new(Guid.NewGuid(), originInstanceId), token).ConfigureAwait(false);
            if (response is not Outcome<AndroidArrivalFrame>.Success { Value: var frame })
            {
                Reset();
                return response;
            }
            foreach (var (peer, currentGeneration) in Apply(frame))
            {
                await shell.OnAndroidPeerArrivalAsync(peer, currentGeneration, token).ConfigureAwait(false);
                await media.LoadArrivedPeerAsync(shell, peer, token).ConfigureAwait(false);
            }
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Reset();
            return new Outcome<AndroidArrivalFrame>.Cancelled();
        }
        finally { lane.Release(); }
    }

    private List<(Guid PeerId, long Generation)> Apply(AndroidArrivalFrame frame)
    {
        lock (gate)
        {
            var ready = new List<(Guid, long)>();
            var next = new Dictionary<Guid, LiveConnection>();
            if (!frame.Baseline && epoch == frame.Epoch && revision is { } after && frame.Revision >= after)
            {
                foreach (var peer in frame.Connections.Where(peer => peer.Connected && peer.ArrivedAtRevision is not null)
                    .OrderBy(peer => peer.ArrivedAtRevision))
                {
                    if (peer.ArrivedAtRevision > after)
                    {
                        var id = checked(++generation);
                        next.Add(peer.PeerId, new(peer.ArrivedAtRevision!.Value, id));
                        ready.Add((peer.PeerId, id));
                    }
                    else if (live.TryGetValue(peer.PeerId, out var known) && known.ArrivedAt == peer.ArrivedAtRevision)
                        next.Add(peer.PeerId, known);
                }
            }
            live = next;
            epoch = frame.Epoch;
            revision = frame.Revision;
            return ready;
        }
    }

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

    private sealed record LiveConnection(long ArrivedAt, long Generation);
}
