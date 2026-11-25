using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Submits one saved playlist to the selected device and player profile.</summary>
/// <remarks>2026-11-24: Define the versioned staging request for FR-FILESYNC-001#ac-playlist-drop.</remarks>
public sealed record StageDevicePlaylistRequest(Guid OriginInstanceId, Guid OperationId, Guid PlaylistId, Guid DeviceId, string ProfileId,
    long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision);

/// <summary>Returns the durable plan and the operation that produced it.</summary>
public sealed record StageDevicePlaylistResponse(Guid OperationId, DeviceExportPlan Plan);

/// <summary>Describes a versioned device stage rejection or dependency failure.</summary>
public sealed record StageDevicePlaylistProblem(string Code, long? ActualRevision = null);

/// <summary>Defines the version 1 route for staging a playlist on a selected device.</summary>
/// <remarks>2026-11-24: Bind the host-owned staging boundary for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public static class DevicePlaylistStageRoute
{
    /// <summary>Identifies the host route template for a saved playlist drop.</summary>
    public const string Pattern = "/api/v1/libraries/{libraryId:guid}/device-exports/stage-playlist";

    /// <summary>Returns the relative request path for one library.</summary>
    public static string Path(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/device-exports/stage-playlist";
}

/// <summary>Reports availability at the exact device destination.</summary>
public enum ExportAudioStatus { Present, Staged, Missing, Unavailable }

/// <summary>Identifies a staged item and its owned relative destination path.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExportEntry.Audio), "audio")]
[JsonDerivedType(typeof(ExportEntry.Playlist), "playlist")]
public abstract record ExportEntry(Guid EntryId, string Destination)
{
    /// <summary>Represents a staged audio transfer.</summary>
    public sealed record Audio(Guid EntryId, string Destination, Guid TrackId, ExportAudioStatus Status) : ExportEntry(EntryId, Destination)
    {
        /// <summary>Whether the track was selected directly, independently of any playlist that needs the same file.</summary>
        /// <remarks>2026-11-24: Preserve direct intent during STB10 staged removal for FR-FILESYNC-001#ac-safety.</remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsDirect { get; init; }
    }

    /// <summary>Represents a generated playlist and its ordered references.</summary>
    public sealed record Playlist(Guid EntryId, string Destination, Guid PlaylistId, string Format,
        ImmutableArray<ExportTrackReference> Tracks) : ExportEntry(EntryId, Destination);
}

/// <summary>Preserves each playlist occurrence at a resolved device destination.</summary>
public sealed record ExportTrackReference(Guid TrackId, string Destination, ExportAudioStatus Status);

/// <summary>Contains the current or requested device export list at a specific profile revision.</summary>
public sealed record DeviceExportPlan(Guid? PlanId, long? PlanRevision, Guid DeviceId, string ProfileId, long ProfileRevision,
    ImmutableArray<ExportEntry> Entries);
