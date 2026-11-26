using BeetsMan.Desktop.Application.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns the first-run or repair state and draft configuration.</summary>
/// <remarks>2026-11-24: Bind FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair, including the logical wait cursor during startup.</remarks>
public sealed partial class BootstrapViewModel(PresentationServices services, Guid ownerId) : PresentationViewModel(services, ownerId)
{
    private BootstrapState? state;
    private bool editingLocations;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyLocationsCommand), nameof(UseDefaultsCommand))]
    private BootstrapSettings? proposedSettings;

    [ObservableProperty]
    private DatabaseCreationApproval? databaseApproval;

    /// <summary>Gets the applied bootstrap or repair snapshot.</summary>
    public BootstrapState? State => state;

    /// <summary>Gets whether the user is editing local locations.</summary>
    public bool EditingLocations => editingLocations;

    [RelayCommand]
    private Task LoadAsync(CancellationToken token) => LoadStateAsync(false, token);

    [RelayCommand]
    private Task EditLocationsAsync(CancellationToken token) => LoadStateAsync(true, token);

    [RelayCommand]
    private Task BackToDefaultsAsync(CancellationToken token) => LoadStateAsync(false, token);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task RetryAsync(CancellationToken token) =>
        WriteAsync(new RetryDesktopBootstrap(), Apply, token, Accepts, blocksInteraction: true);

    [RelayCommand(CanExecute = nameof(CanUseDefaults))]
    private Task UseDefaultsAsync(CancellationToken token) => (state?.Proposed ?? ProposedSettings) is { } defaults
        ? WriteAsync(new InitializeDesktop(defaults, ApprovalFor(defaults)), Apply, token, Accepts, blocksInteraction: true)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanApplyLocations))]
    private Task ApplyLocationsAsync(CancellationToken token) => state is { } snapshot && ProposedSettings is { } proposed
        ? WriteAsync(new SaveBootstrapSettings(proposed, snapshot.Revision, ApprovalFor(proposed)), Apply, token, Accepts,
            blocksInteraction: snapshot.Status != BootstrapStatus.Ready)
        : Task.CompletedTask;

    protected override void OnWriteAvailabilityChanged()
    {
        RetryCommand.NotifyCanExecuteChanged();
        ApplyLocationsCommand.NotifyCanExecuteChanged();
        UseDefaultsCommand.NotifyCanExecuteChanged();
    }

    private bool CanChange() => CanWrite;
    private bool CanApplyLocations() => CanWrite && state is not null && ProposedSettings is not null;
    private bool CanUseDefaults() => CanWrite && (state?.Proposed ?? ProposedSettings) is not null;
    private DatabaseCreationApproval? ApprovalFor(BootstrapSettings settings) =>
        DatabaseApproval is { } approval && state?.CreationProposal == approval &&
        string.Equals(approval.DatabasePath, settings.DatabasePath, StringComparison.Ordinal) ? approval : null;

    private Task LoadStateAsync(bool edit, CancellationToken token) => ReadAsync(new GetBootstrapState(), "bootstrap", incoming =>
    {
        Apply(incoming);
        SetProperty(ref editingLocations, edit, nameof(EditingLocations));
    }, token, Accepts);

    private bool Accepts(BootstrapState incoming) => state is null || incoming.Revision >= state.Revision;

    private void Apply(BootstrapState incoming)
    {
        SetProperty(ref state, incoming, nameof(State));
        ProposedSettings = incoming.Proposed ?? incoming.Effective ?? ProposedSettings;
        if (DatabaseApproval != incoming.CreationProposal)
            DatabaseApproval = null;
        OnWriteAvailabilityChanged();
    }
}

/// <summary>Projects structured, redacted logs and the platform log-folder handoff.</summary>
/// <remarks>2026-11-24: Bind diagnostics for FR-CONFIG-001#ac-logs.</remarks>
/// <remarks>2026-11-25: Enable only the diagnostics query in the repair scope for FR-CONFIG-001#ac-logs.</remarks>
public sealed partial class DiagnosticsViewModel(PresentationServices services, Guid ownerId, bool canOpenLogFolder = true)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private DiagnosticsFilter filter = new();
    [ObservableProperty] private DiagnosticsPage? page;
    [ObservableProperty] private FolderOpenReceipt? logFolder;

    [RelayCommand]
    private Task LoadAsync(string? cursor, CancellationToken token) =>
        ReadAsync(new GetDiagnostics(Filter, cursor), "diagnostics", result => Page = result, token);

    [RelayCommand]
    private Task ClearFilterAsync(CancellationToken token)
    {
        Filter = new();
        return LoadAsync(null, token);
    }

    [RelayCommand(CanExecute = nameof(CanOpenLogFolder))]
    private Task OpenLogFolderAsync(CancellationToken token) => CanOpenLogFolder()
        ? WriteAsync(new OpenLogFolder(), result => LogFolder = result, token)
        : Task.CompletedTask;

    protected override void OnWriteAvailabilityChanged() => OpenLogFolderCommand.NotifyCanExecuteChanged();

    private bool CanOpenLogFolder() => canOpenLogFolder && CanWrite;
}
