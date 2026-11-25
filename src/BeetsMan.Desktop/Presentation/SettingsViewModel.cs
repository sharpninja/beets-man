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
public sealed partial class SettingsViewModel(PresentationServices services, Guid ownerId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private PreferenceCategorySnapshot? category;
    [ObservableProperty] private EffectivePreference? preference;
    [ObservableProperty] private PreferenceResetPlan? resetPlan;
    [ObservableProperty] private MediaSyncState? devices;

    [RelayCommand]
    private Task OpenCategoryAsync(string categoryId, CancellationToken token) => ReadCategoryAsync(categoryId, token);

    [RelayCommand]
    private Task RefreshPreferenceAsync(string key, CancellationToken token) => ReadAsync(new GetPreference(key), "preference", ApplyPreference, token,
        result => result.Key == key && (Preference?.Key != key || result.Revision >= Preference.Revision));

    [RelayCommand]
    private Task ApplyAppearanceAsync(PreferenceEdit edit, CancellationToken token) =>
        WriteAsync(new SetLocalOverride(edit.Key, edit.Value, edit.Revision), ApplyPreference, token, result => result.Key == edit.Key);

    [RelayCommand]
    private Task InheritAppearanceAsync(EffectivePreference preference, CancellationToken token) =>
        WriteAsync(new ClearLocalOverride(preference.Key, preference.Revision), ApplyPreference, token, result => result.Key == preference.Key);

    [RelayCommand]
    private Task EditImportDefaultsAsync(string categoryId, CancellationToken token) => ReadCategoryAsync(categoryId, token);

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
        WriteAsync(new SetSharedPreferences(changes.CategoryId, changes.Changes, changes.Revision), result => Category = result, token,
            result => result.CategoryId == changes.CategoryId && AcceptsCategoryWrite(result));

    [RelayCommand]
    private Task PreviewResetAsync(string categoryId, CancellationToken token) =>
        ReadAsync(new GetPreferenceResetPlan(categoryId), "reset", result => ResetPlan = result, token,
            result => result.CategoryId == categoryId && (Category is null || Category.CategoryId == categoryId));

    [RelayCommand(CanExecute = nameof(CanConfirmReset))]
    private Task ConfirmResetAsync(CancellationToken token) => ResetPlan is { } plan
        ? WriteAsync(new ResetLocalPreferences(plan.PlanId, plan.Revision), result =>
        {
            Category = result;
            ResetPlan = null;
        }, token, result => result.CategoryId == plan.CategoryId && AcceptsCategoryWrite(result)) : Task.CompletedTask;

    protected override void OnWriteAvailabilityChanged() => ConfirmResetCommand.NotifyCanExecuteChanged();

    partial void OnResetPlanChanged(PreferenceResetPlan? value) => ConfirmResetCommand.NotifyCanExecuteChanged();

    private bool CanConfirmReset() => CanWrite && ResetPlan is not null;
    private bool AcceptsCategoryWrite(PreferenceCategorySnapshot result) => Category is null ||
        Category.CategoryId == result.CategoryId && result.Revision >= Category.Revision;

    private Task ReadCategoryAsync(string categoryId, CancellationToken token) => ReadAsync(new GetPreferenceCategory(categoryId), "category", result =>
    {
        if (Category?.CategoryId != result.CategoryId)
            ResetPlan = null;
        Category = result;
    }, token, result => result.CategoryId == categoryId && (Category?.CategoryId != categoryId || result.Revision >= Category.Revision));

    private Task ApplyLocalAsync(PreferenceBatch changes, CancellationToken token) =>
        WriteAsync(new SetLocalPreferences(changes.CategoryId, changes.Changes, changes.Revision), result => Category = result, token,
            result => result.CategoryId == changes.CategoryId && AcceptsCategoryWrite(result));

    private void ApplyPreference(EffectivePreference incoming)
    {
        if (Preference?.Key == incoming.Key && Preference.Revision > incoming.Revision)
            return;
        Preference = incoming;
        if (Category is { } current)
        {
            var existing = current.Preferences.FirstOrDefault(item => item.Key == incoming.Key);
            if (existing is not null && incoming.Revision >= existing.Revision)
                Category = current with { Preferences = current.Preferences.Replace(existing, incoming) };
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

    [RelayCommand]
    private Task OpenCaptureAsync(string scope, CancellationToken token) => LoadScopeAsync(scope, token);

    [RelayCommand]
    private Task ChooseAnotherAsync(string scope, CancellationToken token) => LoadScopeAsync(scope, token);

    [RelayCommand]
    private Task AssignCapturedAsync(ShortcutAssignment assignment, CancellationToken token)
    {
        if (!CanWrite)
            return Task.CompletedTask;
        PendingAssignment = assignment;
        var binding = assignment.Binding;
        return WriteAsync(new AssignShortcut(binding.ActionId, binding.Scope, binding.Gesture, assignment.Revision), result =>
        {
            State = result;
            PendingAssignment = null;
        }, token, result => result.Scope == binding.Scope && Accepts(result));
    }

    [RelayCommand]
    private Task ResetBindingsAsync(CancellationToken token) => State is { } current
        ? WriteAsync(new ResetShortcuts(current.Scope, current.Revision), result => State = result, token,
            result => result.Scope == current.Scope && Accepts(result)) : Task.CompletedTask;

    [RelayCommand]
    private Task SetSystemPlaybackKeysAsync(SystemShortcutSelection selection, CancellationToken token) =>
        WriteAsync(new SetGlobalPlaybackShortcuts(selection.Enabled, selection.Bindings, selection.Revision),
            result => State = result, token, Accepts);

    private Task LoadScopeAsync(string scope, CancellationToken token) => ReadAsync(new GetShortcutBindings(scope), "bindings",
        result => State = result, token, result => result.Scope == scope && Accepts(result));

    private bool Accepts(ShortcutSnapshot incoming) => State?.Scope != incoming.Scope || incoming.Revision >= State.Revision;
}
