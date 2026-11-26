using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Extensions;

public sealed partial class PluginWorkerSupervisor
{
    /// <summary>Reads a negotiated panel and its current activation, if its action is still available.</summary>
    internal (Guid Activation, PluginPanelDefinition Definition)? FindPanel(string id, string version, string panelId)
    {
        lock (gate)
        {
            Reconcile(registry);
            var entry = entries.GetValueOrDefault((id, version));
            return entry?.Worker?.Contributions.FirstOrDefault(item => item.ContributionId == panelId)?.Panel is { } panel
                ? (entry.ActivationId, panel) : null;
        }
    }

    /// <summary>Invokes an action on the current worker and rechecks its identity before admitting a host effect.</summary>
    /// <remarks>2026-11-25: Reject replies from retired versions for FR-PLUGINS-001#ac-visualize and ac-recovery.</remarks>
    internal async Task<Outcome<PluginActionReceipt>> InvokeActionAsync(InvokePluginAction command, Guid operationId, PluginPanelReference? panel,
        Func<PluginContribution, PluginWorkerActionResult, Task<Outcome<PluginActionReceipt>>> apply, CancellationToken token)
    {
        var key = (command.PluginId, command.Version);
        IPluginWorker worker;
        PluginContribution definition;
        lock (gate)
        {
            Reconcile(registry);
            var entry = entries.GetValueOrDefault(key);
            if (shutdown is not null || entry?.Worker is not { } active ||
                active.Contributions.FirstOrDefault(item => item.Kind == PluginContributionKind.Action &&
                    item.ContributionId == command.ContributionId) is not { } granted)
                return PluginActionHandlers.Invalid("plugin-action-unavailable");
            if (panel is not null)
            {
                if (entry.ActivationId != panel.ActivationId || !active.Contributions.Any(item => item.ContributionId == panel.PanelId &&
                        item.Panel?.ActionId == command.ContributionId))
                    return PluginActionHandlers.Invalid("stale-plugin-panel");
            }
            else if (entry.Package.Contributions.Any(item => item.Panel?.ActionId == command.ContributionId))
                return PluginActionHandlers.Invalid("plugin-panel-context-required");
            worker = active;
            definition = granted;
        }
        var result = await worker.InvokeActionAsync(operationId, command.ContributionId, command.Arguments, token).ConfigureAwait(false);
        if (result is not Outcome<PluginWorkerActionResult>.Success reply)
            return PluginActionHandlers.Forward(result);

        Task<Outcome<PluginActionReceipt>> admitted;
        lock (gate)
        {
            Reconcile(registry);
            if (token.IsCancellationRequested)
                return new Outcome<PluginActionReceipt>.Cancelled();
            if (entries.GetValueOrDefault(key)?.Worker != worker)
                return PluginActionHandlers.Invalid("plugin-action-no-longer-authorized");
            admitted = apply(definition, reply.Value);
        }
        return await admitted.ConfigureAwait(false);
    }
}
