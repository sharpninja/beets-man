using System.Collections.Immutable;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a publish attempt and the reviewed staged revision it may apply.</summary>
/// <remarks>2026-11-24: Retain origin identity and exact approval for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record DeviceExportPublicationIntent(Guid OperationId, Guid PlanId, long PlanRevision, DeviceExportRemovalApproval? Approval = null);

/// <summary>Identifies an applied publication, its manifest revision and its optional scheduled transfer.</summary>
/// <remarks>2026-11-24: Schedule a sender scan with the publication receipt for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record CompletedDeviceExportPublication(Guid PublicationId, Guid PlanId, long PlanRevision, long ManifestRevision, Guid? TransferId = null);

/// <summary>Contains enough state to resume an interrupted job at its first unconfirmed operation.</summary>
public sealed record PendingDeviceExportPublication(Guid PublicationId, HostOperationKey Operation, Guid PlanId, long PlanRevision,
    RegisteredAndroidExport Destination, long ManifestRevision, int NextStep, ImmutableArray<DeviceExportFileOperation> Operations);

/// <summary>Describes the existing result or the next permitted part of a publication.</summary>
public abstract record DeviceExportPublicationReservation
{
    private DeviceExportPublicationReservation() { }

    /// <summary>The exact prepared removal proposal still needs approval.</summary>
    public sealed record ConfirmationRequired(DeviceExportRemovalProposal Proposal) : DeviceExportPublicationReservation;

    /// <summary>The authorized intent is durably recorded and ready to apply.</summary>
    public sealed record Pending(PendingDeviceExportPublication Work) : DeviceExportPublicationReservation;

    /// <summary>The original result is already committed.</summary>
    public sealed record Completed(CompletedDeviceExportPublication Receipt) : DeviceExportPublicationReservation;
}

/// <summary>Owns publication reservations, step progress, and the device-scoped ownership manifest.</summary>
/// <remarks>2026-11-24: Track interrupted publication without advancing ownership early for FR-FILESYNC-001#ac-safety.</remarks>
public interface IDeviceExportPublicationJournal
{
    /// <summary>Reads the last committed ownership manifest for the current registered share.</summary>
    Task<HostOutcome<PublishedDeviceExportManifest>> ReadPublishedManifestAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid deviceId,
        CancellationToken token);

    /// <summary>Finds the durable result or remaining work for the same operation without recalculating a partially applied preview.</summary>
    Task<HostOutcome<DeviceExportPublicationReservation?>> FindPublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, CancellationToken token);

    /// <summary>Rechecks scoped approval, current plan and previous ownership, then reserves one job per share.</summary>
    Task<HostOutcome<DeviceExportPublicationReservation>> ReservePublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, PreparedDeviceExportPublication preview, CancellationToken token);

    /// <summary>Records the next verified filesystem action. Its ordinal is zero-based.</summary>
    Task<HostOutcome<int>> RecordPublicationStepAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid publicationId, int ordinal,
        CancellationToken token);

    /// <summary>Commits the new manifest and original receipt after every effect is durably confirmed.</summary>
    Task<HostOutcome<CompletedDeviceExportPublication>> CompletePublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, Guid publicationId, CancellationToken token);
}

/// <summary>Applies one confirmed operation from durable publication work.</summary>
/// <remarks>2026-11-24: Reapply a verified step safely after interruption for FR-FILESYNC-001#ac-safety.</remarks>
public interface IDeviceExportStepWriter
{
    /// <summary>Verifies the resulting bytes or absence before returning success.</summary>
    Task<HostOutcome<bool>> ApplyAsync(PendingDeviceExportPublication work, int ordinal, CancellationToken token);
}

/// <summary>Observes only the configured active share for a registered device export.</summary>
/// <remarks>2026-11-24: Bind separate export roots in the host runtime for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public interface IDeviceExportShareReader
{
    /// <summary>Returns the observed paths and hashes within the registered share.</summary>
    Task<HostOutcome<DeviceExportShareSnapshot>> ReadAsync(RegisteredAndroidExport target, IEnumerable<string> paths, CancellationToken token);
}

/// <summary>Opens the immutable source bytes identified in an export action.</summary>
public interface IDeviceExportContentReader
{
    /// <summary>Opens a private staged copy. The caller owns the returned stream.</summary>
    ValueTask<Stream?> OpenReadAsync(OwnedDeviceExportFile file, CancellationToken token);
}
