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
/// <remarks>2026-11-25: Preserve the inherited shared value while a local theme is applied for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed record EffectivePreference(string Key, PreferenceValue Value, PreferenceSource Source, long Revision, bool CanEditLocal, bool CanEditShared)
{
    /// <summary>Gets the cached host value when the library has a shared default for this key.</summary>
    public PreferenceValue? SharedDefault { get; init; }
}

/// <summary>Returns one settings category and its local and host revisions.</summary>
public sealed record PreferenceCategorySnapshot(string CategoryId, long Revision, long? HostRevision, ImmutableArray<EffectivePreference> Preferences);

/// <summary>Describes local keys to reset and the values they will inherit.</summary>
/// <remarks>2026-11-25: Capture both values and revisions shown in the STB07 reset review for FR-SETTINGS-001.</remarks>
public sealed record PreferenceResetPlan(Guid PlanId, string CategoryId, long Revision, ImmutableArray<EffectivePreference> Inherited,
    ImmutableArray<EffectivePreference> Local = default, long? HostRevision = null);

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
/// <remarks>2026-11-25: Carry the portal's actual trigger description for FR-SETTINGS-001#ac-shortcuts.</remarks>
public sealed record ShortcutBinding(string ActionId, string Scope, string Gesture, string? SystemTriggerDescription = null);

/// <summary>Returns contextual shortcuts and actual system registration.</summary>
/// <remarks>2026-11-25: Keep local binding revisions and actual platform registration independent for FR-SETTINGS-001#ac-shortcuts.</remarks>
public sealed record ShortcutSnapshot(string Scope, long Revision, ImmutableArray<ShortcutBinding> Bindings, bool GlobalPlaybackKeysEnabled,
    ShortcutBinding? WinningBinding = null, long SystemRevision = 0, ImmutableArray<ShortcutBinding> RegisteredSystemBindings = default);

/// <summary>Reads shortcut bindings for a scope.</summary>
public sealed record GetShortcutBindings(string Scope, string? Gesture = null) : IQuery<ShortcutSnapshot>;

/// <summary>Assigns a captured gesture in the given scope.</summary>
public sealed record AssignShortcut(string ActionId, string Scope, string Gesture, long ExpectedRevision) : ICommand<ShortcutSnapshot>;

/// <summary>Restores the defaults for a shortcut scope.</summary>
public sealed record ResetShortcuts(string Scope, long ExpectedRevision) : ICommand<ShortcutSnapshot>;

/// <summary>Requests opt-in platform registration for supported playback actions.</summary>
public sealed record SetGlobalPlaybackShortcuts(bool Enabled, ImmutableArray<ShortcutBinding> RequestedBindings, long ExpectedRevision,
    string Scope = ShortcutScopes.All) : ICommand<ShortcutSnapshot>;

/// <summary>Resolves a gesture against the active dialog and then the supplied active views in precedence order.</summary>
/// <remarks>2026-11-25: Resolve the winning local binding for FR-SETTINGS-001#ac-shortcuts.</remarks>
public sealed record ResolveContextualShortcut(ImmutableArray<string> ActiveScopes, string Gesture) : IQuery<ShortcutResolution>;

/// <summary>Returns the binding that owns the gesture in the active context, if one is assigned.</summary>
public sealed record ShortcutResolution(long Revision, ShortcutBinding? Binding);
