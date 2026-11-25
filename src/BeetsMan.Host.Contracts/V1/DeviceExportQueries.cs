using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies an optional exact plan and the selected playback profile.</summary>
/// <remarks>2026-11-24: Read FR-FILESYNC-001#ac-layouts and ac-playlist-drop through a registered device scope.</remarks>
public sealed record DeviceExportPlanLookup(Guid DeviceId, Guid? PlanId, string? ProfileId, long? ProfileRevision);

/// <summary>Describes a validated player layout and suggested receiver folder.</summary>
public sealed record DeviceLayoutChoice(string ProfileId, long Revision, string Name, string RelativePathTemplate,
    string? PlaylistFormat, string SuggestedRoot);

/// <summary>Returns available profiles for a registered Android device.</summary>
public sealed record DeviceLayoutChoices(Guid DeviceId, ImmutableArray<DeviceLayoutChoice> Profiles)
{
    /// <summary>Gets the last published profile, or the default for a device with no publication.</summary>
    /// <remarks>2026-11-24: Keep the active layout independent of STB20 pending changes for FR-FILESYNC-001#ac-layouts.</remarks>
    public DeviceLayoutAssignment? ActiveProfile { get; init; }
}

/// <summary>Identifies the exact version of the active Android player layout.</summary>
public sealed record DeviceLayoutAssignment(string ProfileId, long Revision);

/// <summary>Defines version 1 queries for layouts and saved export plans.</summary>
public static class DeviceExportQueryRoutes
{
    /// <summary>Identifies the registered player's layout list route.</summary>
    public const string LayoutsPattern = "/api/v1/libraries/{libraryId:guid}/device-exports/{deviceId:guid}/layouts";

    /// <summary>Identifies the selected device's plan route.</summary>
    public const string PlanPattern = "/api/v1/libraries/{libraryId:guid}/device-exports/{deviceId:guid}/plan";

    /// <summary>Returns the relative layout query path.</summary>
    public static string LayoutsPath(Guid libraryId, Guid deviceId) => $"api/v1/libraries/{libraryId:D}/device-exports/{deviceId:D}/layouts";

    /// <summary>Returns the relative staged plan query path.</summary>
    public static string PlanPath(Guid libraryId, Guid deviceId) => $"api/v1/libraries/{libraryId:D}/device-exports/{deviceId:D}/plan";
}
