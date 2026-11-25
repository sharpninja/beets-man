using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Reports a registered peer's current connection and any live-arrival revision.</summary>
/// <remarks>2026-11-24: Bind STB21 to FR-FILESYNC-001#ac-arrival.</remarks>
public sealed record AndroidArrivalConnection(Guid PeerId, bool Connected, long? ArrivedAtRevision);

/// <summary>Carries the registered host's current connection state, feed epoch and revision.</summary>
public sealed record AndroidArrivalFrame(Guid Epoch, long Revision, bool Baseline, ImmutableArray<AndroidArrivalConnection> Connections);

/// <summary>Reads authenticated Android arrival frames without depending on Avalonia.</summary>
public interface IAndroidArrivalHost
{
    /// <summary>Reads a fresh baseline or waits for changes after the current host cursor.</summary>
    Task<Outcome<AndroidArrivalFrame>> ReadArrivalsAsync(Guid? epoch, long? after, RequestContext context, CancellationToken token);
}
