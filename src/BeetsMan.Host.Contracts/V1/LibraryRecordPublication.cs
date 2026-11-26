using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Contains one explicitly shareable change to a registered library.</summary>
/// <remarks>2026-11-25: Scope listening records and eligible defaults for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
[JsonDerivedType(typeof(LibraryRecordIntent.Favorite), "favorite")]
[JsonDerivedType(typeof(LibraryRecordIntent.Play), "play")]
[JsonDerivedType(typeof(LibraryRecordIntent.SharedDefault), "shared-default")]
public abstract record LibraryRecordIntent
{
    private LibraryRecordIntent() { }

    /// <summary>Sets or removes the shared library favorite at its last observed revision.</summary>
    public sealed record Favorite(Guid TrackId, bool IsFavorite, long ExpectedRevision) : LibraryRecordIntent;

    /// <summary>Appends a listening record by its original stable play ID.</summary>
    public sealed record Play(Guid PlayId, Guid TrackId, DateTimeOffset PlayedAtUtc) : LibraryRecordIntent;

    /// <summary>Sets a registered typed default or clears it to inherit the built-in value.</summary>
    /// <remarks>2026-11-25: Add a notification flag without changing the serialized legacy duration intent for FR-SETTINGS-001#ac-availability.</remarks>
    /// <remarks>2026-11-25: Carry the STB07 theme as optional text for FR-SETTINGS-001#ac-precedence.</remarks>
    public sealed record SharedDefault(string Key, long? DurationTicks, long ExpectedRevision,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Flag = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null) : LibraryRecordIntent;
}

/// <summary>Submits one typed change with a stable operation from its registered desktop.</summary>
public sealed record PublishLibraryRecordRequest(Guid OriginInstanceId, Guid OperationId, LibraryRecordIntent Change);

/// <summary>Reports the original record revision and feed head of a recorded operation.</summary>
public sealed record LibraryRecordReceipt(Guid LibraryId, Guid OriginInstanceId, Guid OperationId, Guid RecordId,
    long RecordRevision, long FeedRevision);

/// <summary>Defines the shareable defaults understood by this version of host and desktop.</summary>
/// <remarks>2026-11-25: Admit only the automatic fade duration specified in Stage 0 for FR-SETTINGS-001#ac-availability.</remarks>
/// <remarks>2026-11-25: Register the STB07 sync completion flag for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
/// <remarks>2026-11-25: Register the STB07 Appearance theme for FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
public static class SharedLibraryDefaults
{
    public const string AutomaticFadeDuration = "playback.automaticFadeDuration";
    public const string SyncCompleted = "notifications.syncCompleted";
    public const string Theme = "appearance.theme";
    public const string FollowSystem = "system";
    public const string Dark = "dark";
    public const int RegistryVersion = 1;

    /// <summary>Checks the canonical theme values used by the STB07 command and its preview.</summary>
    public static bool IsTheme(string? value) => value is FollowSystem or Dark;

    /// <summary>Validates a registered typed value. Empty values clear the registered shared key.</summary>
    public static bool IsValid(string key, long? durationTicks, bool? flag = null, string? text = null) => key switch
    {
        AutomaticFadeDuration => flag is null && text is null && durationTicks is null or >= 0,
        SyncCompleted => durationTicks is null && text is null,
        Theme => durationTicks is null && flag is null && (text is null || IsTheme(text)),
        _ => false
    };
}

/// <summary>Computes stable feed IDs for the single shared listener and defaults of a registered library.</summary>
public static class LibraryRecordIdentity
{
    /// <summary>Identifies the shared favorite for this library and track.</summary>
    public static Guid Favorite(Guid library, Guid track) => StableId($"favorite/v1/{library:N}/{track:N}");

    /// <summary>Identifies the shared default in this library's registry.</summary>
    public static Guid SharedDefault(Guid library, string key) => StableId($"shared-default/v1/{library:N}/{key}");

    private static Guid StableId(string identity) => new(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
}

/// <summary>Addresses mutation of versioned listening records and default settings.</summary>
public static class LibraryRecordRoutes
{
    public const string PublishPattern = "/api/v1/libraries/{libraryId:guid}/records";

    /// <summary>Addresses the registered host's record writer.</summary>
    public static string PublishPath(Guid library) => $"api/v1/libraries/{library:D}/records";
}
