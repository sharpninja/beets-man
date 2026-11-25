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
public sealed partial class ExtensionsViewModel(PresentationServices services, Guid ownerId, PlaybackPresentation playback,
    IContributionRegistry contributions) : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private PluginState? state;
    [ObservableProperty] private PluginVersion? diagnosticsSelection;

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
    private Task OpenVisualizationPickerAsync(CancellationToken token) => LoadAsync(token);

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

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task RestartWorkerAsync(PluginVersion plugin, CancellationToken token) => Send(new RestartPluginWorker(plugin.PluginId, plugin.Version), token);

    [RelayCommand(CanExecute = nameof(CanSelectVisualization))]
    private Task SelectVisualizationAsync(ContributionInvocation invocation, CancellationToken token) => CanSelectVisualization(invocation)
        ? WriteAsync(new SelectVisualization(playback.SessionId, invocation.ContributionId), playback.Apply, token, playback.Accepts,
            writeOwner: playback.SessionId) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRunContribution))]
    private Task RunContributionAsync(ContributionInvocation invocation, CancellationToken token) => CanRunContribution(invocation)
        ? contributions.Bind(invocation)?.DispatchAsync(this, token) ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Projects supervisor updates and detaches contributions according to the current snapshot.</summary>
    public Task ReceiveAsync(PluginState incoming) => PostAsync(Scope, () =>
    {
        if (Accepts(incoming))
            Apply(incoming);
    });

    internal Task DispatchContributionAsync<TResult>(ICommand<TResult> request, Action<TResult> project, CancellationToken token) =>
        WriteAsync(request, project, token);

    internal Task ReadContributionAsync<TResult>(IQuery<TResult> request, Action<TResult> project, CancellationToken token) =>
        ReadAsync(request, "contribution", project, token);

    protected override void OnWriteAvailabilityChanged()
    {
        EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged(); RestartWorkerCommand.NotifyCanExecuteChanged();
        SelectVisualizationCommand.NotifyCanExecuteChanged(); RunContributionCommand.NotifyCanExecuteChanged();
    }

    private bool CanEnable(PluginVersion? plugin) => CanWrite && Lookup(plugin)?.Trust == PluginTrust.Local;
    private bool CanDisable(PluginVersion? plugin) => CanWrite && Lookup(plugin)?.Enabled == true;

    private bool CanSelectVisualization(ContributionInvocation? invocation) =>
        IsActive && !Services.HasWrite(playback.SessionId) &&
        (playback.State?.Capabilities & PlaybackCapabilities.Visualization) == PlaybackCapabilities.Visualization &&
        HasContribution(invocation, PluginContributionKind.Visualization);

    private bool CanRunContribution(ContributionInvocation? invocation) => CanWrite && HasContribution(invocation, PluginContributionKind.Action);

    private bool HasContribution(ContributionInvocation? invocation, PluginContributionKind kind) => invocation is not null &&
        Lookup(invocation.Plugin) is { Trust: PluginTrust.Local, Worker: PluginWorkerStatus.Ready, Enabled: true } plugin &&
        !plugin.Contributions.IsDefaultOrEmpty && plugin.Contributions.Any(entry => entry.ContributionId == invocation.ContributionId && entry.Kind == kind);

    private PluginInfo? Lookup(PluginVersion? plugin) => plugin is null ? null :
        State?.Plugins.FirstOrDefault(info => info.PluginId == plugin.PluginId && info.Version == plugin.Version);

    private bool Accepts(PluginState incoming) => incoming.Revision >= (State?.Revision ?? 0);
    private void Apply(PluginState incoming)
    {
        State = incoming;
        OnWriteAvailabilityChanged();
    }

    private Task Send(ICommand<PluginState> request, CancellationToken token) => WriteAsync(request, Apply, token, Accepts);
}
