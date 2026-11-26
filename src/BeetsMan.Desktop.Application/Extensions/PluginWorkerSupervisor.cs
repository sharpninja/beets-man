using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Owns worker activation, failure, and retirement independently of navigation and playback.</summary>
/// <remarks>2026-11-25: Supervise exact trusted versions, detach failed contributions, and suppress activation in recovery for FR-PLUGINS-001#ac-recovery.</remarks>
public sealed partial class PluginWorkerSupervisor(IPluginWorkerFactory factory, bool recoveryMode = false,
    Action<PluginPackage, string>? reportFailure = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(string Id, string Version), WorkerEntry> entries = new();
    private readonly Dictionary<string, Task> retirements = new(StringComparer.Ordinal);
    private PluginRegistrySnapshot registry = new(0, []);
    private long revision;
    private Task? shutdown;

    /// <summary>Reconciles the latest registry and returns the current filtered runtime projection.</summary>
    public PluginState Observe(PluginRegistrySnapshot snapshot, string? id = null, string? version = null)
    {
        lock (gate)
        {
            Reconcile(snapshot);
            return Project(id, version);
        }
    }

    /// <summary>Checks live processes against the last committed registry without reading persistence again.</summary>
    public PluginState? Poll(long previousRevision)
    {
        lock (gate)
        {
            Reconcile(registry);
            return revision == previousRevision ? null : Project(null, null);
        }
    }

    /// <summary>Retires any current instance of a trusted version and queues a fresh verification.</summary>
    public Outcome<PluginState> Restart(PluginRegistrySnapshot snapshot, string id, string version)
    {
        lock (gate)
        {
            Reconcile(snapshot);
            if (shutdown is not null || recoveryMode)
                return new Outcome<PluginState>.Invalid([new("plugin-workers-disabled")]);
            var package = registry.Versions.FirstOrDefault(item => Key(item.Package) == (id, version));
            if (package is null || !CanRun(package))
                return new Outcome<PluginState>.Invalid([new("plugin-worker-not-authorized")]);
            if (entries.Remove((id, version), out var previous))
                Retire(previous);
            QueueStart(package);
            Advance();
            return new Outcome<PluginState>.Success(Project(null, null));
        }
    }

    /// <summary>Cancels startup attempts and joins all owned child cleanup before closing the workspace.</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdown is null)
            {
                foreach (var entry in entries.Values)
                    Retire(entry);
                entries.Clear();
                shutdown = Task.WhenAll(retirements.Values);
            }
            return new(shutdown);
        }
    }

    private void Reconcile(PluginRegistrySnapshot snapshot)
    {
        if (shutdown is not null)
            return;
        foreach (var key in retirements.Where(item => item.Value.IsCompletedSuccessfully).Select(item => item.Key).ToArray())
            retirements.Remove(key);
        if (snapshot.Revision > registry.Revision)
        {
            registry = snapshot;
            revision = Math.Max(checked(revision + 1), snapshot.Revision);
        }
        if (recoveryMode)
            return;
        var allowed = registry.Versions.Where(CanRun).ToDictionary(item => Key(item.Package));
        foreach (var (key, entry) in entries.ToArray())
        {
            if (!allowed.TryGetValue(key, out var item) || item.Package.ArchiveSha256 != entry.Package.ArchiveSha256)
            {
                entries.Remove(key);
                Retire(entry);
            }
        }
        foreach (var (key, item) in allowed)
        {
            if (entries.TryGetValue(key, out var entry))
                ObserveEntry(entry);
            else
                QueueStart(item);
        }
    }

    private void QueueStart(InstalledPluginPackage item)
    {
        var entry = new WorkerEntry(item.Package);
        var prior = retirements.GetValueOrDefault(item.Package.PluginId) ?? Task.CompletedTask;
        entry.Attempt = Task.Run(async () =>
        {
            await prior.WaitAsync(entry.Lifetime.Token).ConfigureAwait(false);
            return await factory.StartAsync(item, entry.Lifetime.Token).ConfigureAwait(false);
        });
        entries.Add(Key(item.Package), entry);
    }

    private void ObserveEntry(WorkerEntry entry)
    {
        if (entry.Status == PluginWorkerStatus.Starting && entry.Attempt.IsCompleted)
        {
            if (entry.Attempt.IsCompletedSuccessfully && entry.Attempt.Result is Outcome<IPluginWorker>.Success active)
            {
                entry.Worker = active.Value;
                entry.Status = PluginWorkerStatus.Ready;
                Advance();
            }
            else
            {
                var outcome = entry.Attempt.IsCompletedSuccessfully ? entry.Attempt.Result : null;
                _ = entry.Attempt.Exception;
                Fail(entry, outcome switch
                {
                    Outcome<IPluginWorker>.DependencyFailure failed => failed.Code,
                    Outcome<IPluginWorker>.Invalid invalid => invalid.Issues.FirstOrDefault()?.Code ?? "plugin-worker-start-failed",
                    _ => "plugin-worker-start-failed"
                });
            }
        }
        if (entry.Worker is { HasExited: true } exited)
        {
            entry.Worker = null;
            exited.Dispose();
            Fail(entry, exited.FailureCode ?? "plugin-worker-exited");
        }
    }

    private void Fail(WorkerEntry entry, string code)
    {
        entry.Status = PluginWorkerStatus.Failed;
        entry.Failure = code;
        Advance();
        reportFailure?.Invoke(entry.Package, code);
    }

    private void Retire(WorkerEntry entry)
    {
        var prior = retirements.GetValueOrDefault(entry.Package.PluginId) ?? Task.CompletedTask;
        retirements[entry.Package.PluginId] = Task.Run(async () =>
        {
            await entry.Lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                if (entry.Status == PluginWorkerStatus.Starting && await entry.Attempt.ConfigureAwait(false) is Outcome<IPluginWorker>.Success pending)
                    pending.Value.Dispose();
                else
                    entry.Worker?.Dispose();
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
            finally { entry.Lifetime.Dispose(); }
            await prior.ConfigureAwait(false);
        });
    }

    private PluginState Project(string? id, string? version) => new(revision,
        [.. registry.Versions.Where(item => (id is null || item.Package.PluginId == id) && (version is null || item.Package.Version == version))
            .OrderBy(item => item.Package.PluginId, StringComparer.Ordinal).ThenBy(item => item.Package.Version, StringComparer.Ordinal)
            .Select(item =>
            {
                var live = entries.GetValueOrDefault(Key(item.Package));
                return new PluginInfo(item.Package.PluginId, item.Package.Version, item.Trust, item.Enabled, live?.Status ?? PluginWorkerStatus.Stopped,
                    live?.Worker?.Contributions ?? [], live?.Failure, item.Package.Compatible, live?.Worker is not null ? live.ActivationId : null,
                    live?.Worker?.Visualizations ?? []);
            })], recoveryMode);

    /// <summary>Returns live support for an offered visualization and the activation it belongs to.</summary>
    /// <remarks>2026-11-25: Keep telemetry within the exact approved worker for FR-PLUGINS-001#ac-visualize.</remarks>
    internal (Guid? Activation, IPluginWorker? Worker, string? Failure) FindVisualization(string id, string version, string contribution)
    {
        lock (gate)
        {
            Reconcile(registry);
            var entry = entries.GetValueOrDefault((id, version));
            return entry?.Worker is { } worker && worker.Visualizations.Contains(contribution)
                ? (entry.ActivationId, worker, null) : (null, null, entry?.Failure);
        }
    }

    private static (string Id, string Version) Key(PluginPackage package) => (package.PluginId, package.Version);
    private static bool CanRun(InstalledPluginPackage item) => item is { Enabled: true, Trust: PluginTrust.Local, Package.Compatible: true };
    private void Advance() => revision = checked(revision + 1);

    private sealed class WorkerEntry(PluginPackage package)
    {
        internal Guid ActivationId { get; } = Guid.NewGuid();
        internal PluginPackage Package { get; } = package;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal Task<Outcome<IPluginWorker>> Attempt { get; set; } = null!;
        internal PluginWorkerStatus Status { get; set; } = PluginWorkerStatus.Starting;
        internal IPluginWorker? Worker { get; set; }
        internal string? Failure { get; set; }
    }
}
