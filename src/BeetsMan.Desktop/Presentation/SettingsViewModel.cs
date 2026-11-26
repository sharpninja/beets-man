using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.MediaSync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures one preference edit and its displayed revision.</summary>
public sealed record PreferenceEdit(string Key, PreferenceValue Value, long Revision);

/// <summary>Captures settings edits for their identified registry category.</summary>
public sealed record PreferenceBatch(string CategoryId, ImmutableDictionary<string, PreferenceValue> Changes, long Revision);

/// <summary>Owns category settings, effective sources, and a scoped reset proposal.</summary>
/// <remarks>2026-11-24: Bind the preference writers for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
/// <remarks>2026-11-25: Stage the STB07 theme preview separately from its effective value for FR-SETTINGS-001#ac-precedence.</remarks>
/// <remarks>2026-11-25: Use the existing host configuration editor for SCR29 and FR-BEETS-001#ac-open.</remarks>
public sealed partial class SettingsViewModel(PresentationServices services, Guid ownerId, HostLibraryViewModel? importDefaultsEditor = null)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EffectiveAppearance))] private PreferenceCategorySnapshot? category;
    [ObservableProperty] private EffectivePreference? preference;
    [ObservableProperty] private PreferenceEdit? appearanceDraft;
    [ObservableProperty] private PreferenceResetPlan? resetPlan;
    [ObservableProperty] private MediaSyncState? devices;

    /// <summary>Gets the active theme and its source independently of the theme proposed in the preview.</summary>
    public EffectivePreference? EffectiveAppearance =>
        Category?.Preferences.FirstOrDefault(item => item.Key == AppearancePreferences.Theme);

    /// <summary>Gets the workspace editor that owns the host import defaults, their revision, and save feedback.</summary>
    public HostLibraryViewModel? ImportDefaultsEditor { get; } = importDefaultsEditor;

    [RelayCommand]
    private Task OpenCategoryAsync(string categoryId, CancellationToken token) => ReadCategoryAsync(categoryId, token);

    [RelayCommand]
    private Task RefreshPreferenceAsync(string key, CancellationToken token) => ReadAsync(new GetPreference(key), "preference", ApplyPreference, token,
        result => result.Key == key && (Preference?.Key != key || result.Revision >= Preference.Revision));

    /// <summary>Stages a registered theme for the Appearance preview without dispatching a settings write.</summary>
    [RelayCommand]
    private void StageAppearance(string? theme)
    {
        if (!CanWrite || EffectiveAppearance is not { CanEditLocal: true } current || theme is null)
            return;
        var choice = new PreferenceValue.Text(theme);
        if (PreferenceRegistry.Find(current.Key)?.Accepts(choice) == true)
            AppearanceDraft = new(current.Key, choice, current.Revision);
    }

    /// <summary>Applies the staged or explicitly supplied theme at the revision seen when it was chosen.</summary>
    [RelayCommand]
    private Task ApplyAppearanceAsync(PreferenceEdit? edit, CancellationToken token) =>
        (edit ?? AppearanceDraft) is { Key: AppearancePreferences.Theme } draft
            ? WriteAsync(new SetLocalOverride(draft.Key, draft.Value, draft.Revision), result =>
            {
                ApplyPreference(result);
                if (AppearanceDraft == draft)
                    AppearanceDraft = null;
            }, token, result => result.Key == draft.Key)
            : Task.CompletedTask;

    /// <summary>Clears the current theme override and projects its inherited value.</summary>
    [RelayCommand]
    private Task InheritAppearanceAsync(EffectivePreference? preference, CancellationToken token) =>
        (preference ?? EffectiveAppearance) is { Key: AppearancePreferences.Theme } current
            ? WriteAsync(new ClearLocalOverride(current.Key, current.Revision), result =>
            {
                ApplyPreference(result);
                AppearanceDraft = null;
            }, token, result => result.Key == current.Key)
            : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanEditImportDefaults))]
    private Task EditImportDefaultsAsync(CancellationToken token) => CanEditImportDefaults()
        ? ImportDefaultsEditor!.ReadConfigurationAsync(token) : Task.CompletedTask;

    private bool CanEditImportDefaults() => IsActive && ImportDefaultsEditor is { IsActive: true };

    [RelayCommand]
    private Task OpenDevicesAsync(CancellationToken token) =>
        ReadAsync(new GetMediaSyncState(), "devices", result => Devices = result, token, result => result.Revision >= (Devices?.Revision ?? 0));

    [RelayCommand]
    private Task ApplyNotificationsAsync(PreferenceBatch changes, CancellationToken token) => ApplyLocalAsync(changes, token);

    [RelayCommand]
    private Task SetSyncDefaultsAsync(PreferenceBatch changes, CancellationToken token) => ApplyLocalAsync(changes, token);

    [RelayCommand]
    private Task ApplyLocalDefaultsAsync(PreferenceBatch changes, CancellationToken token) => ApplyLocalAsync(changes, token);

    [RelayCommand]
    private Task ApplySharedDefaultsAsync(PreferenceBatch changes, CancellationToken token) =>
        WriteAsync(new SetSharedPreferences(changes.CategoryId, changes.Changes, changes.Revision), ApplyCategory, token,
            result => result.CategoryId == changes.CategoryId && AcceptsCategoryWrite(result));

    [RelayCommand]
    private Task PreviewResetAsync(string categoryId, CancellationToken token) =>
        ReadAsync(new GetPreferenceResetPlan(categoryId), "reset", result => ResetPlan = result, token,
            result => result.CategoryId == categoryId && (Category is null ||
                Category.CategoryId == categoryId && result.Revision >= Category.Revision && result.HostRevision >= Category.HostRevision));

    [RelayCommand(CanExecute = nameof(CanConfirmReset))]
    private Task ConfirmResetAsync(CancellationToken token) => CanConfirmReset() && ResetPlan is { } plan
        ? WriteAsync(new ResetLocalPreferences(plan.PlanId, plan.Revision), result =>
        {
            ApplyCategory(result);
            if (result.CategoryId == AppearancePreferences.Category)
                AppearanceDraft = null;
            ResetPlan = null;
        }, token, result => result.CategoryId == plan.CategoryId && AcceptsCategoryWrite(result), blocksInteraction: true) : Task.CompletedTask;

    protected override void OnWriteAvailabilityChanged()
    {
        ConfirmResetCommand.NotifyCanExecuteChanged();
        EditImportDefaultsCommand.NotifyCanExecuteChanged();
    }

    partial void OnResetPlanChanged(PreferenceResetPlan? value) => ConfirmResetCommand.NotifyCanExecuteChanged();

    private bool CanConfirmReset() => CanWrite && ResetPlan is { Inherited.IsDefaultOrEmpty: false };
    private bool AcceptsCategoryWrite(PreferenceCategorySnapshot result) => Category is null ||
        Category.CategoryId == result.CategoryId && result.Revision >= Category.Revision && result.HostRevision >= Category.HostRevision;

    private void ApplyCategory(PreferenceCategorySnapshot result)
    {
        if (result.CategoryId != AppearancePreferences.Category)
            AppearanceDraft = null;
        if (ResetPlan is { } plan && (plan.CategoryId != result.CategoryId ||
                plan.Revision != result.Revision || plan.HostRevision != result.HostRevision))
            ResetPlan = null;
        Category = result;
    }

    private Task ReadCategoryAsync(string categoryId, CancellationToken token) =>
        ReadAsync(new GetPreferenceCategory(categoryId), "category", ApplyCategory, token,
            result => result.CategoryId == categoryId && (Category?.CategoryId != categoryId || AcceptsCategoryWrite(result)));

    private Task ApplyLocalAsync(PreferenceBatch changes, CancellationToken token) =>
        WriteAsync(new SetLocalPreferences(changes.CategoryId, changes.Changes, changes.Revision), ApplyCategory, token,
            result => result.CategoryId == changes.CategoryId && AcceptsCategoryWrite(result));

    private void ApplyPreference(EffectivePreference incoming)
    {
        if (Preference?.Key == incoming.Key && Preference.Revision > incoming.Revision)
            return;
        Preference = incoming;
        if (Category is { } current)
        {
            var existing = current.Preferences.FirstOrDefault(item => item.Key == incoming.Key);
            if (existing is not null && incoming.Revision >= current.Revision)
                ApplyCategory(current with { Revision = incoming.Revision,
                    Preferences = [.. current.Preferences.Replace(existing, incoming).Select(item => item with { Revision = incoming.Revision })] });
        }
    }
}

/// <summary>Describes a captured shortcut and its contextual editor revision.</summary>
public sealed record ShortcutAssignment(ShortcutBinding Binding, long Revision);

/// <summary>Contains the explicitly requested system playback bindings.</summary>
public sealed record SystemShortcutSelection(bool Enabled, ImmutableArray<ShortcutBinding> Bindings, long Revision);

/// <summary>Owns contextual gesture capture and opt-in system playback shortcuts.</summary>
/// <remarks>2026-11-24: Bind FR-SETTINGS-001#ac-shortcuts while preserving the existing binding during conflicts.</remarks>
public sealed partial class ShortcutsViewModel(PresentationServices services, Guid ownerId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private ShortcutSnapshot? state;
    [ObservableProperty] private ShortcutAssignment? pendingAssignment;
    [ObservableProperty] private ShortcutBinding? winningBinding;
    [ObservableProperty] private bool requestedSystemPlaybackKeys;

    [RelayCommand]
    private Task OpenCaptureAsync(string scope, CancellationToken token) => StartCaptureAsync(scope, token);

    [RelayCommand]
    private Task ChooseAnotherAsync(string scope, CancellationToken token) => StartCaptureAsync(scope, token);

    [RelayCommand]
    private Task AssignCapturedAsync(ShortcutAssignment assignment, CancellationToken token)
    {
        if (!CanWrite)
            return Task.CompletedTask;
        PendingAssignment = assignment;
        WinningBinding = null;
        var binding = assignment.Binding;
        return WriteAsync(new AssignShortcut(binding.ActionId, binding.Scope, binding.Gesture, assignment.Revision), result =>
        {
            State = result;
            PendingAssignment = null;
        }, token, result => PendingAssignment == assignment && result.Scope == binding.Scope && Accepts(result), afterResult: () =>
            PendingAssignment == assignment && Failure?.Issues.Any(issue => issue.Code == "shortcut-gesture-in-use") == true
                ? ReadAsync(new GetShortcutBindings(binding.Scope, binding.Gesture), "shortcut-conflict",
                    result => WinningBinding = result.WinningBinding, token, _ => PendingAssignment == assignment, preserveFailure: true)
                : Task.CompletedTask);
    }

    [RelayCommand]
    private Task ResetBindingsAsync(CancellationToken token) => State is { } current
        ? WriteAsync(new ResetShortcuts(current.Scope, current.Revision), result => State = result, token,
            result => result.Scope == current.Scope && Accepts(result)) : Task.CompletedTask;

    [RelayCommand]
    private Task SetSystemPlaybackKeysAsync(SystemShortcutSelection selection, CancellationToken token)
    {
        if (!CanWrite)
            return Task.CompletedTask;
        RequestedSystemPlaybackKeys = selection.Enabled;
        return WriteAsync(new SetGlobalPlaybackShortcuts(selection.Enabled, selection.Bindings, selection.Revision, State?.Scope ?? ShortcutScopes.All),
            result => State = result, token, Accepts, blocksInteraction: true);
    }

    private Task StartCaptureAsync(string scope, CancellationToken token)
    {
        PendingAssignment = null;
        WinningBinding = null;
        return LoadScopeAsync(scope, token);
    }

    /// <summary>Updates the accepted system state without changing the open contextual editor.</summary>
    /// <remarks>2026-11-25: Project OS registration changes for FR-SETTINGS-001#ac-shortcuts.</remarks>
    public Task ReceiveSystemRegistrationAsync(ShortcutSnapshot incoming) => PostAsync(Scope, () =>
    {
        if (State is { } current && incoming.SystemRevision > current.SystemRevision)
            State = current with { SystemRevision = incoming.SystemRevision, GlobalPlaybackKeysEnabled = incoming.GlobalPlaybackKeysEnabled,
                RegisteredSystemBindings = incoming.RegisteredSystemBindings };
    });

    private Task LoadScopeAsync(string scope, CancellationToken token) => ReadAsync(new GetShortcutBindings(scope), "bindings",
        result => State = result, token, result => result.Scope == scope && Accepts(result));

    private bool Accepts(ShortcutSnapshot incoming) => State is not { } current ||
        incoming.SystemRevision >= current.SystemRevision && incoming.Revision >= current.Revision;
}
