using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Names the notification preferences shown in STB07.</summary>
/// <remarks>2026-11-25: Register the local and shared flags from FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
public static class NotificationPreferences
{
    public const string Category = "notifications";
    public const string SyncCompleted = SharedLibraryDefaults.SyncCompleted;
    public const string PeerNeedsAttention = "notifications.peerNeedsAttention";
    public const string PlaybackErrors = "notifications.playbackErrors";
}

/// <summary>Identifies the Appearance theme and its setting category.</summary>
/// <remarks>2026-11-25: Share the same key and allowed values as the host for FR-SETTINGS-001#ac-precedence.</remarks>
public static class AppearancePreferences
{
    public const string Category = "appearance";
    public const string Theme = SharedLibraryDefaults.Theme;
}

/// <summary>Describes one allowable preference and its fallback.</summary>
public sealed record PreferenceDefinition(string Key, string Category, PreferenceValue BuiltIn, bool Shareable = false)
{
    /// <summary>Checks values before storing an override or accepting a host default.</summary>
    /// <remarks>2026-11-25: Check the exact theme values from STB07 for FR-SETTINGS-001#ac-precedence.</remarks>
    public bool Accepts(PreferenceValue? value) => value is null || (BuiltIn, value) switch
    {
        (PreferenceValue.Flag, PreferenceValue.Flag) => true,
        (PreferenceValue.Duration, PreferenceValue.Duration duration) => duration.Value >= TimeSpan.Zero,
        (PreferenceValue.Text, PreferenceValue.Text text) => Key == AppearancePreferences.Theme && SharedLibraryDefaults.IsTheme(text.Value),
        _ => false
    };
}

/// <summary>Owns the order, type, built-in value, and shareability of each implemented preference.</summary>
/// <remarks>2026-11-25: Preserve playback preference semantics and add STB07 notification fields for FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-25: Register Follow system as the STB07 theme fallback for FR-SETTINGS-001#ac-precedence.</remarks>
public static class PreferenceRegistry
{
    private static readonly ImmutableArray<PreferenceDefinition> Definitions =
    [
        new(PlaybackPreferences.ResumeAfterRestart, PlaybackPreferences.Category, new PreferenceValue.Flag(false)),
        new(PlaybackPreferences.AutomaticFadeDuration, PlaybackPreferences.Category, new PreferenceValue.Duration(TimeSpan.Zero), Shareable: true),
        new(AppearancePreferences.Theme, AppearancePreferences.Category, new PreferenceValue.Text(SharedLibraryDefaults.FollowSystem), Shareable: true),
        new(NotificationPreferences.SyncCompleted, NotificationPreferences.Category, new PreferenceValue.Flag(true), Shareable: true),
        new(NotificationPreferences.PeerNeedsAttention, NotificationPreferences.Category, new PreferenceValue.Flag(true)),
        new(NotificationPreferences.PlaybackErrors, NotificationPreferences.Category, new PreferenceValue.Flag(true))
    ];

    /// <summary>Looks up a single registered preference by exact key.</summary>
    public static PreferenceDefinition? Find(string key) => Definitions.FirstOrDefault(item => item.Key == key);

    /// <summary>Lists the registered keys in their category order.</summary>
    public static ImmutableArray<PreferenceDefinition> Category(string category) => [.. Definitions.Where(item => item.Category == category)];
}

/// <summary>Captures both optional values of one registered preference in the same repository view.</summary>
public sealed record StoredPreference(PreferenceDefinition Definition, PreferenceValue? Local, PreferenceValue? Shared);

/// <summary>Captures the consistent local and last-known shared revisions of one settings category.</summary>
public sealed record StoredPreferences(string Category, long Revision, long SharedRevision, ImmutableArray<StoredPreference> Preferences);

/// <summary>Exposes scoped local preferences, compare-and-set writes and the shared preference cache.</summary>
/// <remarks>2026-11-25: Compare both reviewed revisions inside the store transaction for FR-SETTINGS-001.</remarks>
public interface IDesktopPreferenceStore
{
    Task<Outcome<StoredPreferences>> ReadPreferencesAsync(Guid library, string category, CancellationToken token);
    Task<Outcome<StoredPreferences>> WritePreferencesAsync(Guid library, string category, long expectedRevision,
        ImmutableDictionary<string, PreferenceValue?> values, CancellationToken token, long? expectedSharedRevision = null);
    Task<Outcome<StoredPreferences>> CacheSharedPreferenceAsync(Guid library, long revision, ReplicaSharedDefault value, CancellationToken token);
}
