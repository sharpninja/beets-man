using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Stages ordered catalog tracks for an exact device, profile, and prior plan revision.</summary>
/// <remarks>2026-11-24: Carry the STB10 direct track drop for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed record StageDeviceTracksRequest(Guid OriginInstanceId, Guid OperationId, ImmutableArray<Guid> OrderedTrackIds,
    Guid DeviceId, string ProfileId, long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision);

/// <summary>Removes selected staged intent from an exact signed plan revision.</summary>
public sealed record RemoveDeviceExportEntriesRequest(Guid OriginInstanceId, Guid OperationId, Guid PlanId,
    ImmutableArray<Guid> EntryIds, long ExpectedPlanRevision);

/// <summary>Returns the plan revision produced by a track drop or staged removal.</summary>
public sealed record DeviceSyncListEditResponse(Guid OperationId, DeviceExportPlan Plan);

/// <summary>Defines the authenticated version 1 Sync List edit routes.</summary>
public static class DeviceSyncListEditRoutes
{
    /// <summary>Registers the direct-track stage route with route binding.</summary>
    public const string StageTracksPattern = "/api/v1/libraries/{libraryId:guid}/device-exports/stage-tracks";

    /// <summary>Registers the staged-entry removal route with route binding.</summary>
    public const string RemovePattern = "/api/v1/libraries/{libraryId:guid}/device-exports/remove-staged";

    /// <summary>Gets the relative URI for the direct-track drop.</summary>
    public static string StageTracksPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/device-exports/stage-tracks";

    /// <summary>Gets the relative URI for a staged-list removal.</summary>
    public static string RemovePath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/device-exports/remove-staged";
}
