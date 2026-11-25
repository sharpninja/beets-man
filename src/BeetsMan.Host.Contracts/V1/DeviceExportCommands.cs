using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Requests a preview of the exact plan, device and player profile.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-layouts and ac-safety across authenticated HTTPS.</remarks>
public sealed record PrepareDeviceExportRequest(Guid OriginInstanceId, Guid OperationId, Guid DeviceId, string ProfileId,
    long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision, ImmutableDictionary<Guid, string>? RelativePathsByTrack);

/// <summary>Submits the reviewed plan revision and any exact owned-removal approval.</summary>
public sealed record PublishDeviceExportRequest(Guid OriginInstanceId, Guid OperationId, Guid PlanId, long PlanRevision,
    ExportRemovalApproval? Approval);

/// <summary>Binds a confirmation to the proposal and reviewed plan revision.</summary>
public sealed record ExportRemovalApproval(Guid ProposalId, Guid PlanId, long PlanRevision);

/// <summary>Reports an issue and the affected share-relative path.</summary>
public sealed record ExportPreviewIssue(string Code, string Path);

/// <summary>Identifies a proposed filesystem action.</summary>
public enum ExportFileChangeKind { Add = 1, Replace = 2, Move = 3, Remove = 4 }

/// <summary>Shows the change from an optional prior path to an optional destination.</summary>
public sealed record ExportFileChange(ExportFileChangeKind Kind, string? Before, string? After);

/// <summary>Returns the reviewed plan, file actions, conflicts and proposed removal paths to the origin.</summary>
public sealed record PrepareDeviceExportResponse(Guid OperationId, DeviceExportPlan Plan, ImmutableArray<ExportFileChange> Changes,
    ImmutableArray<ExportPreviewIssue> Issues, ImmutableArray<string> ProposedRemovals)
{
    /// <summary>Whether this review assigns a new profile on successful publication.</summary>
    /// <remarks>2026-11-24: Bind move removals to STB20 confirmation for FR-FILESYNC-001#ac-safety.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReassignsProfile { get; init; }
}

/// <summary>Returns either the original publication receipt or a proposal awaiting confirmation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DeviceExportCommandResult.Published), "published")]
[JsonDerivedType(typeof(DeviceExportCommandResult.ConfirmationRequired), "confirmation-required")]
public abstract record DeviceExportCommandResult(Guid OperationId, Guid PlanId, long PlanRevision)
{
    /// <summary>Reports host publication and the optional separately scheduled transfer.</summary>
    public sealed record Published(Guid OperationId, Guid PlanId, long PlanRevision, Guid PublicationId, Guid? TransferId)
        : DeviceExportCommandResult(OperationId, PlanId, PlanRevision);

    /// <summary>Reports the exact owned paths that need confirmation before publication.</summary>
    public sealed record ConfirmationRequired(Guid OperationId, Guid PlanId, long PlanRevision, Guid ProposalId,
        ImmutableArray<string> Paths) : DeviceExportCommandResult(OperationId, PlanId, PlanRevision);
}

/// <summary>Defines version 1 routes for reviewing and applying a device export.</summary>
public static class DeviceExportCommandRoutes
{
    public const string PreparePattern = "/api/v1/libraries/{libraryId:guid}/device-exports/prepare-preview";
    public const string PublishPattern = "/api/v1/libraries/{libraryId:guid}/device-exports/publish";

    /// <summary>Returns the relative preview command path for a selected library.</summary>
    public static string PreparePath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/device-exports/prepare-preview";

    /// <summary>Returns the relative publication command path for a selected library.</summary>
    public static string PublishPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/device-exports/publish";
}
