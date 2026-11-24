using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Identifies the layer supplying an effective setting.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-SETTINGS-001#ac-precedence, ac-availability, and ac-shortcuts.</remarks>
public enum PreferenceSource { BuiltIn, HostDefault, LocalOverride }

/// <summary>Carries one typed setting value from the preference registry.</summary>
public abstract record PreferenceValue
{
    public sealed record Flag(bool Value) : PreferenceValue;
    public sealed record Count(long Value) : PreferenceValue;
    public sealed record Text(string Value) : PreferenceValue;
    public sealed record Duration(TimeSpan Value) : PreferenceValue;
}

/// <summary>Returns the effective setting and its editable revision.</summary>
public sealed record EffectivePreference(string Key, PreferenceValue Value, PreferenceSource Source, long Revision, bool CanEditLocal, bool CanEditShared);

/// <summary>Returns one settings category and its local and host revisions.</summary>
public sealed record PreferenceCategorySnapshot(string CategoryId, long Revision, long? HostRevision, ImmutableArray<EffectivePreference> Preferences);

/// <summary>Describes local keys to reset and the values they will inherit.</summary>
public sealed record PreferenceResetPlan(Guid PlanId, string CategoryId, long Revision, ImmutableArray<EffectivePreference> Inherited);

/// <summary>Reads one preference by its registered key.</summary>
public sealed record GetPreference(string Key) : IQuery<EffectivePreference>;

/// <summary>Reads a category from the preference registry.</summary>
public sealed record GetPreferenceCategory(string CategoryId) : IQuery<PreferenceCategorySnapshot>;

/// <summary>Writes one scoped local override.</summary>
public sealed record SetLocalOverride(string Key, PreferenceValue Value, long ExpectedRevision) : ICommand<EffectivePreference>;

/// <summary>Removes the scoped local override and returns the inherited value.</summary>
public sealed record ClearLocalOverride(string Key, long ExpectedRevision) : ICommand<EffectivePreference>;

/// <summary>Writes a batch of preference changes to the local layer.</summary>
public sealed record SetLocalPreferences(string CategoryId, ImmutableDictionary<string, PreferenceValue> Changes, long ExpectedRevision) : ICommand<PreferenceCategorySnapshot>;

/// <summary>Writes a batch of eligible shared preference changes at the host.</summary>
public sealed record SetSharedPreferences(string CategoryId, ImmutableDictionary<string, PreferenceValue> Changes, long ExpectedHostRevision) : ICommand<PreferenceCategorySnapshot>;

/// <summary>Reads a scoped reset proposal.</summary>
public sealed record GetPreferenceResetPlan(string CategoryId) : IQuery<PreferenceResetPlan>;

/// <summary>Commits the identified local reset plan.</summary>
public sealed record ResetLocalPreferences(Guid PlanId, long ExpectedRevision) : ICommand<PreferenceCategorySnapshot>;

/// <summary>Identifies a registered shortcut action and gesture.</summary>
public sealed record ShortcutBinding(string ActionId, string Scope, string Gesture);

/// <summary>Returns contextual shortcuts and actual system registration.</summary>
public sealed record ShortcutSnapshot(string Scope, long Revision, ImmutableArray<ShortcutBinding> Bindings, bool GlobalPlaybackKeysEnabled, ShortcutBinding? WinningBinding = null);

/// <summary>Reads shortcut bindings for a scope.</summary>
public sealed record GetShortcutBindings(string Scope) : IQuery<ShortcutSnapshot>;

/// <summary>Assigns a captured gesture in the given scope.</summary>
public sealed record AssignShortcut(string ActionId, string Scope, string Gesture, long ExpectedRevision) : ICommand<ShortcutSnapshot>;

/// <summary>Restores the defaults for a shortcut scope.</summary>
public sealed record ResetShortcuts(string Scope, long ExpectedRevision) : ICommand<ShortcutSnapshot>;

/// <summary>Requests opt-in platform registration for supported playback actions.</summary>
public sealed record SetGlobalPlaybackShortcuts(bool Enabled, ImmutableArray<ShortcutBinding> RequestedBindings, long ExpectedRevision) : ICommand<ShortcutSnapshot>;
