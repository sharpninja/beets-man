using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies the specific installed version being operated on.</summary>
public sealed record PluginVersion(string PluginId, string Version);

/// <summary>Identifies an approved contribution and its declarative arguments.</summary>
public sealed record ContributionInvocation(PluginVersion Plugin, string ContributionId, ImmutableDictionary<string, string> Arguments);

/// <summary>Identifies the panel contributed by one installed plugin version.</summary>
public sealed record PluginPanelSelection(PluginVersion Plugin, string PanelId);

/// <summary>Binds enabled descriptors to pre-registered, typed application requests.</summary>
public interface IContributionRegistry
{
    /// <summary>Validates arguments against the descriptor and binds the registered request and projector.</summary>
    ContributionRoute? Bind(ContributionInvocation invocation);
}

/// <summary>Preserves a concrete CQRS result while exposing a single Toolkit command property.</summary>
public abstract class ContributionRoute
{
    internal abstract Task DispatchAsync(ExtensionsViewModel owner, CancellationToken token);

    /// <summary>Creates a binding for a registered command.</summary>
    public static ContributionRoute Command<TResult>(ICommand<TResult> request, Action<TResult> project) =>
        new CommandRoute<TResult>(request, project);

    /// <summary>Creates a binding for a registered query.</summary>
    public static ContributionRoute Query<TResult>(IQuery<TResult> request, Action<TResult> project) =>
        new QueryRoute<TResult>(request, project);

    private sealed class CommandRoute<TResult>(ICommand<TResult> request, Action<TResult> project) : ContributionRoute
    {
        internal override Task DispatchAsync(ExtensionsViewModel owner, CancellationToken token) =>
            owner.DispatchContributionAsync(request, project, token);
    }

    private sealed class QueryRoute<TResult>(IQuery<TResult> request, Action<TResult> project) : ContributionRoute
    {
        internal override Task DispatchAsync(ExtensionsViewModel owner, CancellationToken token) =>
            owner.ReadContributionAsync(request, project, token);
    }
}

/// <summary>Owns local package management, worker diagnostics, and approved contributions.</summary>
/// <remarks>2026-11-24: Bind FR-PLUGINS-001#ac-manage, ac-visualize and ac-recovery without invoking workers from presentation.</remarks>
/// <remarks>2026-11-25: Project the typed plugin action and optional playback result for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed partial class ExtensionsViewModel(PresentationServices services, Guid ownerId, PlaybackPresentation playback,
    IContributionRegistry? contributions = null) : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private PluginState? state;
    [ObservableProperty] private PluginVersion? diagnosticsSelection;
    [ObservableProperty] private PluginActionReceipt? lastAction;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SelectVisualizationCommand))]
    private VisualizationState? visualization;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPanelCommand), nameof(ClosePanelCommand))]
    private PluginPanelSnapshot? panel;
    private long panelRequest;

    [RelayCommand]
    private Task LoadAsync(CancellationToken token) => ReadAsync(new GetPluginState(), "plugins", Apply, token, Accepts);

    [RelayCommand]
    private Task InspectDiagnosticsAsync(PluginVersion plugin, CancellationToken token) =>
        ReadAsync(new GetPluginState(plugin.PluginId, plugin.Version), "diagnostics", result =>
        {
            var others = State?.Plugins.Where(info => info.PluginId != plugin.PluginId || info.Version != plugin.Version) ?? [];
            Apply(result with { Plugins = [.. others, .. result.Plugins] });
            DiagnosticsSelection = plugin;
        }, token, Accepts);

    [RelayCommand]
    private Task OpenVisualizationPickerAsync(CancellationToken token) => Task.WhenAll(LoadAsync(token),
        ReadAsync(new GetVisualization(playback.SessionId), "visualization", ApplyVisualization, token, AcceptsVisualization));

    [RelayCommand]
    private Task InstallAsync(Guid selection, CancellationToken token) => Send(new InstallPluginPackage(selection), token);

    [RelayCommand]
    private Task UpdateAsync(Guid selection, CancellationToken token) => Send(new InstallPluginPackage(selection), token);

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableAsync(PluginVersion plugin, CancellationToken token) => Send(new SetPluginEnabled(plugin.PluginId, plugin.Version, true), token);

    [RelayCommand(CanExecute = nameof(CanDisable))]
    private Task DisableAsync(PluginVersion plugin, CancellationToken token) => Send(new SetPluginEnabled(plugin.PluginId, plugin.Version, false), token);

    [RelayCommand]
    private Task TrustLocallyAsync(PluginVersion plugin, CancellationToken token) => Send(new SetPluginTrust(plugin.PluginId, plugin.Version, PluginTrust.Local), token);

    [RelayCommand]
    private Task RemoveAsync(PluginVersion plugin, CancellationToken token) => Send(new RemovePlugin(plugin.PluginId, plugin.Version), token);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartWorkerAsync(PluginVersion plugin, CancellationToken token) => Send(new RestartPluginWorker(plugin.PluginId, plugin.Version), token);

    [RelayCommand(CanExecute = nameof(CanSelectVisualization))]
    private Task SelectVisualizationAsync(VisualizationChoice choice, CancellationToken token) => CanSelectVisualization(choice)
        ? WriteAsync(new SelectVisualization(playback.SessionId, choice), ApplyVisualization, token, AcceptsVisualization)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRunContribution))]
    private Task RunContributionAsync(ContributionInvocation invocation, CancellationToken token)
    {
        if (!CanRunContribution(invocation))
            return Task.CompletedTask;
        if (contributions is not null)
            return contributions.Bind(invocation)?.DispatchAsync(this, token) ?? Task.CompletedTask;
        return WriteAsync(new InvokePluginAction(playback.SessionId, invocation.Plugin.PluginId, invocation.Plugin.Version,
            invocation.ContributionId, invocation.Arguments), ApplyAction, token);
    }

    [RelayCommand(CanExecute = nameof(CanOpenPanel))]
    private Task OpenPanelAsync(PluginPanelSelection selection, CancellationToken token)
    {
        if (!CanOpenPanel(selection))
            return Task.CompletedTask;
        var request = ++panelRequest;
        return ReadAsync(new GetPluginPanel(playback.SessionId, selection.Plugin.PluginId, selection.Plugin.Version, selection.PanelId),
            "plugin-panel", opened =>
            {
                Panel = opened;
                LastAction = null;
            }, token, opened => request == panelRequest && IsCurrentPanel(opened.Reference));
    }

    [RelayCommand(CanExecute = nameof(CanSubmitPanel))]
    private Task SubmitPanelAsync(ImmutableDictionary<string, string> fields, CancellationToken token) => Panel is { } current &&
        CanSubmitPanel(fields) ? WriteAsync(new SubmitPluginPanel(current.Reference, fields), ApplyAction, token,
            _ => Panel == current && IsCurrentPanel(current.Reference), blocksInteraction: true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanClosePanel))]
    private void ClosePanel()
    {
        panelRequest++;
        Panel = null;
        LastAction = null;
    }

    /// <summary>Projects supervisor updates and detaches contributions according to the current snapshot.</summary>
    public Task ReceiveAsync(PluginState incoming) => PostAsync(Scope, () =>
    {
        if (Accepts(incoming))
            Apply(incoming);
    });

    /// <summary>Projects current frames only for the attached desktop session.</summary>
    /// <remarks>2026-11-25: Keep telemetry revisions separate from the transport for FR-PLUGINS-001#ac-visualize.</remarks>
    public Task ReceiveAsync(VisualizationState incoming) => PostAsync(Scope, () =>
    {
        if (AcceptsVisualization(incoming))
            ApplyVisualization(incoming);
    });

    internal Task DispatchContributionAsync<TResult>(ICommand<TResult> request, Action<TResult> project, CancellationToken token) =>
        WriteAsync(request, project, token);

    internal Task ReadContributionAsync<TResult>(IQuery<TResult> request, Action<TResult> project, CancellationToken token) =>
        ReadAsync(request, "contribution", project, token);

    protected override void OnWriteAvailabilityChanged()
    {
        EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged(); RestartWorkerCommand.NotifyCanExecuteChanged();
        SelectVisualizationCommand.NotifyCanExecuteChanged(); RunContributionCommand.NotifyCanExecuteChanged();
        OpenPanelCommand.NotifyCanExecuteChanged(); SubmitPanelCommand.NotifyCanExecuteChanged();
    }

    private bool CanEnable(PluginVersion? plugin) => CanWrite && Lookup(plugin)?.Trust == PluginTrust.Local;
    private bool CanDisable(PluginVersion? plugin) => CanWrite && Lookup(plugin)?.Enabled == true;
    private bool CanRestart(PluginVersion? plugin) => CanEnable(plugin) && State?.RecoveryMode != true && Lookup(plugin)?.Enabled == true;

    private bool CanSelectVisualization(VisualizationChoice? choice)
    {
        if (choice is null || !CanWrite || Visualization?.Supported != true)
            return false;
        return choice == VisualizationChoice.Waveform || choice == VisualizationChoice.Spectrum ||
            choice is { PluginId: { } id, Version: { } version } &&
            Lookup(new(id, version)) is { Worker: PluginWorkerStatus.Ready, Visualizations.IsDefaultOrEmpty: false } ready &&
            ready.Visualizations.Contains(choice.ContributionId);
    }

    private bool CanRunContribution(ContributionInvocation? invocation) => invocation is not null && CanWrite &&
        HasContribution(invocation, PluginContributionKind.Action) &&
        !Lookup(invocation.Plugin)!.Contributions.Any(item => item.Panel?.ActionId == invocation.ContributionId);

    private bool CanSubmitPanel(ImmutableDictionary<string, string>? fields) =>
        fields is not null && CanWrite && Panel is { } current && IsCurrentPanel(current.Reference);

    private bool CanOpenPanel(PluginPanelSelection? selection) => selection is not null && IsActive &&
        Lookup(selection.Plugin) is { Worker: PluginWorkerStatus.Ready } current &&
        current.Contributions.Any(item => item.ContributionId == selection.PanelId && item.Panel is not null);

    private bool CanClosePanel() => Panel is not null;

    private bool IsCurrentPanel(PluginPanelReference reference) =>
        Lookup(new(reference.PluginId, reference.Version)) is { Worker: PluginWorkerStatus.Ready } current &&
        current.ActivationId == reference.ActivationId && current.Contributions.Any(item => item.ContributionId == reference.PanelId && item.Panel is not null);

    private bool HasContribution(ContributionInvocation? invocation, PluginContributionKind kind) => invocation is not null &&
        Lookup(invocation.Plugin) is { Trust: PluginTrust.Local, Worker: PluginWorkerStatus.Ready, Enabled: true } plugin &&
        !plugin.Contributions.IsDefaultOrEmpty && plugin.Contributions.Any(entry => entry.ContributionId == invocation.ContributionId && entry.Kind == kind);

    private PluginInfo? Lookup(PluginVersion? plugin) => plugin is null ? null :
        State?.Plugins.FirstOrDefault(info => info.PluginId == plugin.PluginId && info.Version == plugin.Version);

    private bool Accepts(PluginState incoming) => incoming.Revision >= (State?.Revision ?? 0);
    private bool AcceptsVisualization(VisualizationState incoming) =>
        incoming.SessionId == playback.SessionId && incoming.Revision >= (Visualization?.Revision ?? 0);

    private void ApplyVisualization(VisualizationState incoming) => Visualization = incoming;
    private void Apply(PluginState incoming)
    {
        State = incoming;
        if (Panel is { } open && !IsCurrentPanel(open.Reference))
            ClosePanel();
        OnWriteAvailabilityChanged();
    }

    private void ApplyAction(PluginActionReceipt result)
    {
        LastAction = result;
        if (result.Playback is { } update && playback.Accepts(update))
            playback.Apply(update);
    }

    private Task Send(ICommand<PluginState> request, CancellationToken token) => WriteAsync(request, Apply, token, Accepts);
}
