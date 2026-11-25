namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies the host-observed transfer phase for a committed export.</summary>
/// <remarks>2026-11-24: Expose verified receiver progress for FR-FILESYNC-001#ac-safety.</remarks>
public enum DeviceExportTransferPhase { Queued = 1, AwaitingReceiver = 2, Complete = 3, Paused = 4, Failed = 5 }

/// <summary>Contains the transfer, media share, progress and retry eligibility without private receiver connection data.</summary>
public sealed record DeviceExportTransferCheckpoint(Guid TransferId, Guid ShareId, long Revision, DeviceExportTransferPhase Phase,
    long BytesVerified, long? TotalBytes, string? FailureCode, bool Retryable);

/// <summary>Requests retry of a specific revision using the same desktop operation identity across attempts.</summary>
public sealed record RetryDeviceExportTransferRequest(Guid OriginInstanceId, Guid OperationId, long ExpectedRevision);

/// <summary>Returns the original accepted retry checkpoint for replay.</summary>
public sealed record RetryDeviceExportTransferResponse(Guid OperationId, DeviceExportTransferCheckpoint Checkpoint);

/// <summary>Identifies version 1 HTTP routes for transfer inspection and retry.</summary>
public static class DeviceExportTransferRoutes
{
    public const string ReadPattern = "/api/v1/libraries/{libraryId:guid}/device-exports/transfers/{transferId:guid}";
    public const string RetryPattern = ReadPattern + "/retry";

    /// <summary>Returns the relative authenticated inspection path.</summary>
    public static string ReadPath(Guid libraryId, Guid transferId) => $"api/v1/libraries/{libraryId:D}/device-exports/transfers/{transferId:D}";

    /// <summary>Returns the relative authenticated retry path.</summary>
    public static string RetryPath(Guid libraryId, Guid transferId) => ReadPath(libraryId, transferId) + "/retry";
}
