using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Owns a successfully negotiated worker until its registered version stops or exits.</summary>
/// <remarks>2026-11-25: Expose process lifetime and approved descriptors for FR-PLUGINS-001#ac-visualize and ac-recovery.</remarks>
public interface IPluginWorker : IDisposable
{
    ImmutableArray<PluginContribution> Contributions { get; }
    bool HasExited { get; }
    string? FailureCode { get; }

    /// <summary>Invokes a negotiated action through the worker's serialized request channel.</summary>
    Task<Outcome<PluginWorkerActionResult>> InvokeActionAsync(Guid operationId, string contributionId, ImmutableDictionary<string, string> arguments,
        CancellationToken token);

    /// <summary>Lists the negotiated destinations for bounded telemetry protocol version 1.</summary>
    /// <remarks>2026-11-25: Require explicit worker support for FR-PLUGINS-001#ac-visualize.</remarks>
    ImmutableArray<string> Visualizations { get; }

    /// <summary>Offers the latest frame, or skips the exchange when the worker is already busy.</summary>
    Task<Outcome<ImmutableArray<float>?>> RenderVisualizationAsync(Guid sessionId, string contributionId,
        PlaybackVisualizationFrame frame, CancellationToken token);
}

/// <summary>Prepares and negotiates a worker for an exact locally enabled package.</summary>
public interface IPluginWorkerFactory
{
    Task<Outcome<IPluginWorker>> StartAsync(InstalledPluginPackage package, CancellationToken token);
}
