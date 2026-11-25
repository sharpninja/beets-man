using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Exposes published transfer progress and retry through the authenticated host.</summary>
/// <remarks>2026-11-24: Map the existing transfer CQRS contract for FR-FILESYNC-001#ac-safety.</remarks>
public interface IDeviceExportTransferStore
{
    /// <summary>Reads a transfer visible to an active registered caller in its library.</summary>
    Task<HostOutcome<DeviceExportTransferCheckpoint>> ReadTransferAsync(Guid libraryId, AuthenticatedHostInstance caller,
        Guid transferId, DateTimeOffset now, CancellationToken token);

    /// <summary>Makes retryable work due immediately and persists the exact response for replay.</summary>
    Task<HostOutcome<RetryDeviceExportTransferResponse>> RetryTransferAsync(Guid libraryId, AuthenticatedHostInstance caller,
        Guid transferId, RetryDeviceExportTransferRequest request, DateTimeOffset now, CancellationToken token);
}
