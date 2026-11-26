using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Reads coherent pages and advances acknowledgements only for the authenticated recipient.</summary>
/// <remarks>2026-11-24: Define the host repository for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
internal interface IHostCatalogReplication
{
    Task<HostOutcome<CatalogFeedPage>> ReadCatalogChangesAsync(Guid library, AuthenticatedHostInstance caller, long after, long? upper,
        int limit, CancellationToken token);
    Task<HostOutcome<CatalogNotifications>> ReadCatalogNotificationsAsync(Guid library, AuthenticatedHostInstance caller, int limit, CancellationToken token);
    Task<HostOutcome<CatalogAcknowledgementReceipt>> AcknowledgeCatalogAsync(Guid library, AuthenticatedHostInstance caller,
        CatalogAcknowledgement request, CancellationToken token);
    /// <summary>Captures the replacement catalog and returns the first ordered page.</summary>
    Task<HostOutcome<CatalogSnapshotPage>> BeginCatalogSnapshotAsync(Guid library, AuthenticatedHostInstance caller, BeginCatalogSnapshot request,
        CancellationToken token);
    /// <summary>Reads a page of the recipient's previously captured replacement catalog.</summary>
    Task<HostOutcome<CatalogSnapshotPage>> ReadCatalogSnapshotAsync(Guid library, AuthenticatedHostInstance caller, Guid snapshotId, int offset, int limit,
        CancellationToken token);
}
