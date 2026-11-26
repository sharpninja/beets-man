using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Presentation;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Owns first-run configuration and the diagnostics available during repair.</summary>
/// <remarks>2026-11-25: Supply the pre-workspace binding for FR-CONFIG-001#ac-defaults, ac-repair and ac-logs.</remarks>
public sealed record DesktopSetupPresentation(BootstrapViewModel Configuration, DiagnosticsViewModel Diagnostics);

public sealed partial class DesktopStartup
{
    /// <summary>Builds the setup surface with the typed commands that open the workspace when ready.</summary>
    /// <remarks>2026-11-25: Preserve the wait state through activation for FR-CONFIG-001#ac-repair.</remarks>
    private DesktopSetupPresentation ComposeSetup(IUiScheduler scheduler)
    {
        var commands = new SetupCommands(this);
        var routes = new RequestDispatcher.Builder()
            .RegisterQuery<GetBootstrapState, BootstrapState>(bootstrap)
            .RegisterQuery<GetDiagnostics, DiagnosticsPage>(recoveryDiagnostics)
            .RegisterCommand<RetryDesktopBootstrap, BootstrapState>(commands)
            .RegisterCommand<InitializeDesktop, BootstrapState>(commands)
            .RegisterCommand<SaveBootstrapSettings, BootstrapState>(commands)
            .Build();
        var services = new PresentationServices(routes, scheduler, origin);
        return new(new(services, Guid.NewGuid()), new(services, Guid.NewGuid(), canOpenLogFolder: false));
    }

    /// <summary>Runs a setup command and completes any required activation before returning its typed outcome.</summary>
    /// <remarks>2026-11-25: Keep configuration and workspace activation under one lifecycle owner for FR-CONFIG-001#ac-repair.</remarks>
    private async Task<Outcome<BootstrapState>> ApplySetupAsync(ICommand<BootstrapState> command, CommandContext context, CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var configured = await Bootstrap.SendAsync(command, context, token).ConfigureAwait(false);
            var result = await CompleteStartupAsync(configured, context, token).ConfigureAwait(false);
            return result is Outcome<DesktopStartupState>.Success { Value: var ready }
                ? new Outcome<BootstrapState>.Success(ready.Bootstrap)
                : ForwardFailure<DesktopStartupState, BootstrapState>(result);
        }
        finally { lane.Release(); }
    }

    /// <summary>Routes the interactive setup commands through the desktop lifecycle owner.</summary>
    /// <remarks>2026-11-25: Preserve the typed requests for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
    private sealed class SetupCommands(DesktopStartup owner) : ICommandHandler<RetryDesktopBootstrap, BootstrapState>,
        ICommandHandler<InitializeDesktop, BootstrapState>, ICommandHandler<SaveBootstrapSettings, BootstrapState>
    {
        public Task<Outcome<BootstrapState>> HandleAsync(RetryDesktopBootstrap command, CommandContext context, CancellationToken token) =>
            owner.ApplySetupAsync(command, context, token);

        public Task<Outcome<BootstrapState>> HandleAsync(InitializeDesktop command, CommandContext context, CancellationToken token) =>
            owner.ApplySetupAsync(command, context, token);

        public Task<Outcome<BootstrapState>> HandleAsync(SaveBootstrapSettings command, CommandContext context, CancellationToken token) =>
            owner.ApplySetupAsync(command, context, token);
    }
}
