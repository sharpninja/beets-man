using System.Collections.Immutable;

namespace BeetsMan.Host.Application;

/// <summary>Identifies an expected path effect and its pinned sender index version.</summary>
/// <remarks>2026-11-24: Verify the exact publication on the receiver for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record DeviceExportTransferItem(string Path, string? ContentIdentity, long? Bytes = null, string? Version = null);

/// <summary>Contains the scope and immutable file effects to check on the receiver.</summary>
public sealed record DeviceExportReceiverLease(DeviceExportScanLease Lease, ImmutableArray<DeviceExportTransferItem> Items);

/// <summary>Reports bytes verified and whether every path including removals arrived.</summary>
public sealed record DeviceExportReceiverProgress(long BytesVerified, long TotalBytes, bool Completed);

/// <summary>Stores pending receiver checks and their stable evidence.</summary>
public interface IDeviceExportReceiverStore
{
    /// <summary>Leases the next eligible receiver check.</summary>
    Task<HostOutcome<DeviceExportReceiverLease?>> ClaimReceiverAsync(DateTimeOffset now, CancellationToken token);

    /// <summary>Records sender proof and an optional receiver observation for the same lease.</summary>
    Task<HostOutcome<bool>> RecordReceiverAsync(DeviceExportReceiverLease work, ImmutableArray<DeviceExportTransferItem> items,
        DeviceExportReceiverProgress? progress, string? failureCode, bool blocked, DateTimeOffset now, CancellationToken token);
}

/// <summary>Reads scoped sender and receiver evidence for a particular publication.</summary>
public interface IDeviceExportReceiverVerifier
{
    /// <summary>Binds the reviewed sender paths to versions in a stable local Syncthing index.</summary>
    Task<HostOutcome<ImmutableArray<DeviceExportTransferItem>>> CaptureSenderAsync(RegisteredAndroidExport target,
        ImmutableArray<DeviceExportTransferItem> items, CancellationToken token);

    /// <summary>Counts received bytes and verifies that every expected path effect arrived.</summary>
    Task<HostOutcome<DeviceExportReceiverProgress>> ObserveReceiverAsync(RegisteredAndroidExport target,
        ImmutableArray<DeviceExportTransferItem> items, CancellationToken token);
}

/// <summary>Checks published effects until the receiver confirms the transfer.</summary>
/// <remarks>2026-11-24: Pin sender evidence once and recover receiver checkpoints for FR-FILESYNC-001#ac-safety.</remarks>
public sealed class DeviceExportReceiverDispatcher(IDeviceExportReceiverStore store, IDeviceExportReceiverVerifier verifier, TimeProvider time)
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Records one due receiver check, including partial progress where observed.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var next = await store.ClaimReceiverAsync(time.GetUtcNow(), token).ConfigureAwait(false);
        if (next is not HostOutcome<DeviceExportReceiverLease?>.Success { Value: var work })
            return next.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);

        var target = work.Lease.Checkpoint.Destination;
        var items = work.Items;
        using var timeout = new CancellationTokenSource(AttemptTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        if (items.Any(item => item.Bytes is null))
        {
            var captured = await verifier.CaptureSenderAsync(target, items, linked.Token).ConfigureAwait(false);
            if (captured is not HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Success { Value: var proofs })
            {
                var (code, blocked) = Failure(captured);
                return await store.RecordReceiverAsync(work, items, null, code, blocked, time.GetUtcNow(), token).ConfigureAwait(false);
            }
            items = proofs;
        }

        var observed = await verifier.ObserveReceiverAsync(target, items, linked.Token).ConfigureAwait(false);
        if (observed is HostOutcome<DeviceExportReceiverProgress>.Success { Value: var progress })
            return await store.RecordReceiverAsync(work, items, progress, null, false, time.GetUtcNow(), token).ConfigureAwait(false);
        var (failure, terminal) = Failure(observed);
        return await store.RecordReceiverAsync(work, items, null, failure, terminal, time.GetUtcNow(), token).ConfigureAwait(false);
    }

    private static (string Code, bool Blocked) Failure<TResult>(HostOutcome<TResult> result) => result switch
    {
        HostOutcome<TResult>.Rejected rejected => (rejected.Code, true),
        HostOutcome<TResult>.Forbidden => ("export-receiver-access-denied", true),
        HostOutcome<TResult>.Unavailable unavailable => (unavailable.Code, false),
        _ => ("export-receiver-unavailable", false)
    };
}
