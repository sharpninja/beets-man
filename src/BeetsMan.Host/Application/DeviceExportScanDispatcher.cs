namespace BeetsMan.Host.Application;

/// <summary>Identifies the state of a scheduled sender scan.</summary>
/// <remarks>2026-11-24: Separate sender acknowledgement from actual receiver progress for FR-FILESYNC-001#ac-safety.</remarks>
public enum DeviceExportScanState { Queued, AwaitingReceiver, Blocked, Superseded, Completed }

/// <summary>Describes the durable record linked to a committed device export.</summary>
public sealed record DeviceExportScanCheckpoint(Guid TransferId, Guid PlanId, long PlanRevision, long ManifestRevision, long Revision,
    RegisteredAndroidExport Destination, DeviceExportScanState State, int Attempts, string? FailureCode, long BytesVerified = 0, long? TotalBytes = null);

/// <summary>Reserves an eligible transfer for one bounded scan attempt.</summary>
public sealed record DeviceExportScanLease(DeviceExportScanCheckpoint Checkpoint, Guid LeaseId);

/// <summary>Stores the committed scan request, exclusive lease and retry checkpoint.</summary>
public interface IDeviceExportScanStore
{
    /// <summary>Reads the transfer within the authenticated library.</summary>
    Task<HostOutcome<DeviceExportScanCheckpoint>> ReadScanAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid transferId, CancellationToken token);

    /// <summary>Claims the next ready request that still owns its registered share revision.</summary>
    Task<HostOutcome<DeviceExportScanLease?>> ClaimScanAsync(DateTimeOffset now, CancellationToken token);

    /// <summary>Records the result for the same lease or discards a superseded attempt.</summary>
    Task<HostOutcome<bool>> RecordScanAsync(DeviceExportScanLease lease, DeviceExportScanState state, string? failureCode, DateTimeOffset now,
        CancellationToken token);
}

/// <summary>Requests a scan from the exact configured Syncthing sender folder.</summary>
public interface IDeviceExportSenderScanner
{
    /// <summary>Returns success after the sender acknowledges the scan.</summary>
    Task<HostOutcome<bool>> ScanAsync(RegisteredAndroidExport target, CancellationToken token);
}

/// <summary>Dispatches ready scans and checkpoints their outcomes for durable replay.</summary>
/// <remarks>2026-11-24: Resume committed exports after a restart without recreating publication effects for FR-FILESYNC-001#ac-safety.</remarks>
public sealed class DeviceExportScanDispatcher(IDeviceExportScanStore store, IDeviceExportSenderScanner scanner, TimeProvider time)
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Attempts one due scan, returning whether an attempt was recorded.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var selected = await store.ClaimScanAsync(time.GetUtcNow(), token).ConfigureAwait(false);
        if (selected is not HostOutcome<DeviceExportScanLease?>.Success { Value: var lease })
            return selected.PropagateFailure<bool>();
        if (lease is null)
            return new HostOutcome<bool>.Success(false);

        using var timeout = new CancellationTokenSource(AttemptTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var result = await scanner.ScanAsync(lease.Checkpoint.Destination, linked.Token).ConfigureAwait(false);
        var (state, code) = result switch
        {
            HostOutcome<bool>.Success { Value: true } => (DeviceExportScanState.AwaitingReceiver, (string?)null),
            HostOutcome<bool>.Rejected rejected => (DeviceExportScanState.Blocked, rejected.Code),
            HostOutcome<bool>.Forbidden => (DeviceExportScanState.Blocked, "export-scan-access-denied"),
            HostOutcome<bool>.Unavailable unavailable => (DeviceExportScanState.Queued, unavailable.Code),
            _ => (DeviceExportScanState.Queued, "export-scan-unavailable")
        };
        return await store.RecordScanAsync(lease, state, code, time.GetUtcNow(), token).ConfigureAwait(false);
    }
}
