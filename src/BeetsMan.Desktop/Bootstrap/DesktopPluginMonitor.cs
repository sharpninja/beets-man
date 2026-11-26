using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Processes;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Presentation;
using Microsoft.Extensions.Logging;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Runs plugin health reconciliation for the active desktop without depending on a screen.</summary>
/// <remarks>2026-11-25: Start and stop worker supervision with the workspace for FR-PLUGINS-001#ac-recovery.</remarks>
internal sealed class DesktopPluginMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? running;

    internal DesktopPluginMonitor(LocalPluginPackages packages, bool recoveryMode, ILogger logger)
    {
        Supervisor = new(new PluginWorkerFactory(packages), recoveryMode, (package, code) =>
        {
            using var scope = logger.BeginScope(new DiagnosticScope(null, package.PluginId, package.Version));
            var eventId = code switch
            {
                "plugin-worker-exited" => 5401,
                "plugin-worker-timeout" => 5402,
                "plugin-worker-protocol-invalid" => 5403,
                _ => 5404
            };
            logger.LogWarning(new EventId(eventId, code), "Plugin worker failed");
        });
    }

    internal PluginWorkerSupervisor Supervisor { get; }

    internal void Start(IRequestDispatcher dispatcher, Guid origin, Guid session, VisualizationSession visualization, ExtensionsViewModel? projection) =>
        running = Task.WhenAll(RunAsync(dispatcher, origin, projection, lifetime.Token),
            RunVisualizationAsync(dispatcher, origin, session, visualization, projection, lifetime.Token));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (running is not null)
                await running.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
            await Supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunAsync(IRequestDispatcher dispatcher, Guid origin, ExtensionsViewModel? projection, CancellationToken token)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var lastRevision = -1L;
        var loaded = false;
        try
        {
            do
            {
                PluginState? state;
                if (loaded)
                    state = Supervisor.Poll(lastRevision);
                else
                {
                    var result = await dispatcher.QueryAsync(new GetPluginState(), new(Guid.NewGuid(), origin), token).ConfigureAwait(false);
                    state = (result as Outcome<PluginState>.Success)?.Value;
                    loaded = state is not null;
                }
                if (state is null)
                    continue;
                if (projection is not null)
                    await projection.ReceiveAsync(state).ConfigureAwait(false);
                lastRevision = state.Revision;
            } while (await interval.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Coalesces latest telemetry and publishes only new projection revisions.</summary>
    /// <remarks>2026-11-25: Keep worker rendering independent of playback and health polling for FR-PLUGINS-001#ac-visualize.</remarks>
    private static async Task RunVisualizationAsync(IRequestDispatcher dispatcher, Guid origin, Guid session,
        VisualizationSession visualization, ExtensionsViewModel? projection, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(80));
        var lastRevision = -1L;
        Task rendering = Task.CompletedTask;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (rendering.IsCompleted)
                {
                    await rendering.ConfigureAwait(false);
                    rendering = visualization.ObserveAsync(token);
                }
                if (projection is null)
                    continue;
                var result = await dispatcher.QueryAsync(new GetVisualization(session), new(Guid.NewGuid(), origin), token).ConfigureAwait(false);
                if (result is Outcome<VisualizationState>.Success { Value: var next } && next.Revision != lastRevision)
                {
                    await projection.ReceiveAsync(next).ConfigureAwait(false);
                    lastRevision = next.Revision;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await rendering.ConfigureAwait(false); }
    }
}
