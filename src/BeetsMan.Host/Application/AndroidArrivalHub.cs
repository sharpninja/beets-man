using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Tracks confirmed connections and live arrival revisions within one host process epoch.</summary>
/// <remarks>2026-11-24: Publish STB21 native events without replaying startup or gap history for FR-FILESYNC-001#ac-arrival.</remarks>
internal sealed class AndroidArrivalHub
{
    private readonly object gate = new();
    private readonly Guid epoch = Guid.NewGuid();
    private readonly Dictionary<AndroidArrivalSender, ImmutableDictionary<Guid, ObservedConnection>> sources = [];
    private long revision;
    private TaskCompletionSource changed = NewSignal();

    /// <summary>Updates one native source from a consistent connection observation.</summary>
    public void Publish(AndroidArrivalSender sender, ImmutableArray<RegisteredAndroidExport> targets, IReadOnlyDictionary<string, bool> connections,
        IReadOnlySet<string>? liveArrivals)
    {
        lock (gate)
        {
            var previous = sources.GetValueOrDefault(sender) ?? ImmutableDictionary<Guid, ObservedConnection>.Empty;
            var next = ImmutableDictionary.CreateBuilder<Guid, ObservedConnection>();
            foreach (var target in targets)
            {
                var connected = connections.GetValueOrDefault(target.SyncthingDeviceId);
                var old = previous.GetValueOrDefault(target.DeviceId);
                long? arrival = null;
                if (connected && liveArrivals is not null)
                {
                    if (liveArrivals.Contains(target.SyncthingDeviceId))
                        arrival = checked(revision + 1);
                    else if (old is { Connected: true } && old.Target == target)
                        arrival = old.Arrival;
                }
                next.Add(target.DeviceId, new(target, connected, arrival));
            }
            if (previous.Count == next.Count && previous.All(pair => next.GetValueOrDefault(pair.Key) == pair.Value))
                return;
            sources[sender] = next.ToImmutable();
            revision = checked(revision + 1);
            changed.TrySetResult();
            changed = NewSignal();
        }
    }

    /// <summary>Removes observations after the native event source becomes uncertain.</summary>
    public void Invalidate(AndroidArrivalSender sender) => Publish(sender, [], ImmutableDictionary<string, bool>.Empty, null);

    /// <summary>Waits briefly for a source change after the supplied cursor.</summary>
    public async Task WaitAsync(Guid? requestedEpoch, long? after, CancellationToken token)
    {
        Task signal;
        lock (gate)
        {
            if (requestedEpoch != epoch || after != revision)
                return;
            signal = changed.Task;
        }
        try { await signal.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    /// <summary>Returns current observations only for the still-authorized registry bindings.</summary>
    public AndroidArrivalSnapshot Read(Guid? requestedEpoch, long? after, ImmutableArray<RegisteredAndroidExport> authorized)
    {
        lock (gate)
        {
            var baseline = requestedEpoch != epoch || after is null or < 0 || after > revision;
            var entries = sources.Values.SelectMany(items => items.Values).ToLookup(item => item.Target.DeviceId);
            var peers = ImmutableArray.CreateBuilder<AndroidArrivalPeer>();
            foreach (var target in authorized)
            {
                if (entries[target.DeviceId].Where(item => item.Target == target).Take(2).ToArray() is [var item])
                    peers.Add(new(target.DeviceId, item.Connected, baseline ? null : item.Arrival));
            }
            return new(epoch, revision, baseline, peers.ToImmutable());
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record ObservedConnection(RegisteredAndroidExport Target, bool Connected, long? Arrival);
}

/// <summary>Identifies the configured native sender for a group of Android registrations.</summary>
internal sealed record AndroidArrivalSender(Uri Endpoint, string DeviceId);
