using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Configures the native folder and endpoints for one already registered desktop.</summary>
/// <remarks>2026-11-24: Resolve FR-FILESYNC-001#ac-mirror through local configuration and registered identity.</remarks>
public sealed record PcMirrorHostShare(Guid PeerId, string FolderId, Uri ReceiverEndpoint, string ReceiverRoot, Uri SenderEndpoint, string SenderDeviceId);

/// <summary>Binds the stable app share ID and display name to its current native scope.</summary>
internal sealed record PcMirrorBinding(string Name, RegisteredPcMirror Native)
{
    public Guid ShareId => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"pc-mirror/v1/{Native.LibraryId:N}/{Native.PeerId:N}/{Native.SenderDeviceId}/{Native.ReceiverDeviceId}/{Native.FolderId}")).AsSpan(0, 16));

    public string Scope => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Native)));
}

/// <summary>Preserves the confirmed observation, command fence, and control revision for one native scope.</summary>
internal sealed record StoredPcMirror(string Scope, long Revision, long ConnectionGeneration, PcMirrorNativeState State, string? PendingOperation);

/// <summary>Captures the most recent native configuration observation for a paired Android share.</summary>
/// <remarks>2026-11-24: Preserve refreshed status across other media commands for FR-FILESYNC-001#ac-layouts.</remarks>
internal sealed record AndroidShareObservation(MediaSharePhase Phase, string? FailureCode = null)
{
    /// <summary>Projects verified configuration separately from file transfer progress.</summary>
    public static AndroidShareObservation From(HostOutcome<AndroidShareCondition> result) => result switch
    {
        HostOutcome<AndroidShareCondition>.Success { Value: AndroidShareCondition.AwaitingAcceptance } => new(MediaSharePhase.AwaitingAcceptance),
        HostOutcome<AndroidShareCondition>.Success { Value: AndroidShareCondition.Ready } => new(MediaSharePhase.Ready),
        HostOutcome<AndroidShareCondition>.Success { Value: AndroidShareCondition.Paused } => new(MediaSharePhase.Paused),
        HostOutcome<AndroidShareCondition>.Rejected failure => new(MediaSharePhase.Failed, failure.Code),
        _ => new(MediaSharePhase.Unknown)
    };
}

/// <summary>Identifies the configured PC and Android targets under one authenticated library.</summary>
internal sealed record MediaMirrorDirectory(long Revision, ImmutableDictionary<Guid, StoredPcMirror> Observations,
    ImmutableArray<PcMirrorBinding> Mirrors, ImmutableArray<(RegisteredAndroidExport Target, string Name, AndroidShareObservation Observation)> Android)
{
    public StoredPcMirror? Find(PcMirrorBinding mirror) =>
        Observations.GetValueOrDefault(mirror.ShareId) is { } known && known.Scope == mirror.Scope ? known : null;

    public MediaSyncSnapshot Project(AndroidArrivalHub arrivals)
    {
        var peers = ImmutableArray.CreateBuilder<MediaPeerSnapshot>();
        var shares = ImmutableArray.CreateBuilder<MediaShareSnapshot>();
        foreach (var mirror in Mirrors)
        {
            var observation = Find(mirror);
            var state = observation?.State;
            peers.Add(new(mirror.Native.PeerId, mirror.Name, MediaPeerType.PcMirror, state?.Connected == true,
                state is not null && (state.FailureCode is null || state.Connected), observation?.ConnectionGeneration ?? 0));
            var phase = state?.Phase switch
            {
                PcMirrorPhase.AwaitingAcceptance => MediaSharePhase.AwaitingAcceptance,
                PcMirrorPhase.Paused => MediaSharePhase.Paused,
                PcMirrorPhase.Transferring => MediaSharePhase.Transferring,
                PcMirrorPhase.Complete => MediaSharePhase.Complete,
                PcMirrorPhase.Disconnected => MediaSharePhase.Disconnected,
                PcMirrorPhase.Failed => MediaSharePhase.Failed,
                _ => MediaSharePhase.Unknown
            };
            shares.Add(new(mirror.ShareId, mirror.Native.PeerId, state?.Paused == true, observation?.Revision ?? 0, phase,
                state?.FilesReceived, state?.TotalFiles, state?.FailureCode));
        }
        var observed = arrivals.Read(null, null, [.. Android.Select(entry => entry.Target)]).Peers.ToDictionary(peer => peer.PeerId);
        foreach (var (target, name, folder) in Android)
        {
            var status = observed.GetValueOrDefault(target.DeviceId);
            peers.Add(new(target.DeviceId, name, MediaPeerType.Android, status?.Connected == true, status is not null, 0));
            shares.Add(new(target.MediaShareId, target.DeviceId, folder.Phase == MediaSharePhase.Paused, 0, folder.Phase,
                FailureCode: folder.FailureCode));
        }
        return new(Revision, peers.ToImmutable(), shares.ToImmutable());
    }
}

/// <summary>Identifies a complete original response or authorized work remaining after interruption.</summary>
internal sealed record PcMirrorReceipt(MediaMirrorControlResponse? Completed);

/// <summary>Registers mirrors from host configuration and stores verified observations and command receipts.</summary>
internal interface IPcMirrorStore
{
    Task<HostOutcome<MediaMirrorDirectory>> ReadAsync(Guid libraryId, AuthenticatedHostInstance caller, CancellationToken token);
    Task<HostOutcome<MediaMirrorDirectory>> ObserveAsync(Guid libraryId, AuthenticatedHostInstance caller,
        IReadOnlyList<(PcMirrorBinding Mirror, PcMirrorNativeState State)> observations,
        IReadOnlyList<(RegisteredAndroidExport Target, AndroidShareObservation State)> android, CancellationToken token);
    Task<HostOutcome<PcMirrorReceipt?>> ReadReceiptAsync(Guid libraryId, AuthenticatedHostInstance caller, PcMirrorBinding mirror,
        MediaMirrorControlRequest request, string fingerprint, CancellationToken token);
    Task<HostOutcome<PcMirrorReceipt>> ReserveAsync(Guid libraryId, AuthenticatedHostInstance caller, PcMirrorBinding mirror,
        MediaMirrorControlRequest request, string fingerprint, CancellationToken token);
    Task<HostOutcome<MediaMirrorControlResponse>> CompleteAsync(Guid libraryId, AuthenticatedHostInstance caller, PcMirrorBinding mirror,
        MediaMirrorControlRequest request, string fingerprint, PcMirrorNativeState observation, CancellationToken token);
    /// <summary>Resolves and fences the approved pairing before any native write.</summary>
    Task<HostOutcome<MediaPairingPreparation>> ReservePairingAsync(Guid libraryId, AuthenticatedHostInstance caller,
        MediaPairingRequest request, CancellationToken token);
    /// <summary>Atomically stores the verified binding and original response.</summary>
    Task<HostOutcome<MediaPairingResponse>> CompletePairingAsync(Guid libraryId, AuthenticatedHostInstance caller,
        MediaPairingRequest request, RegisteredMediaPairing target, MediaShareOffer offer, CancellationToken token);
}

/// <summary>Returns either a fenced native offer or the exact original committed result.</summary>
internal sealed record MediaPairingPreparation(RegisteredMediaPairing Target, MediaPairingResponse? Replay);

/// <summary>Coordinates the native mirror and its durable library status before projecting results to the desktop.</summary>
/// <remarks>2026-11-24: Bind authenticated and retryable STB10 commands for FR-FILESYNC-001#ac-mirror.</remarks>
internal sealed class HostPcMirrors(IPcMirrorStore store, IPcMirrorControl native, IMediaShareOffer offers,
    IAndroidShareStatus androidShares, AndroidArrivalHub arrivals) : IDisposable
{
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Offers the approved native share and returns its committed peer projection.</summary>
    /// <remarks>2026-11-24: Hold pairing and mirror controls on the same lane for FR-FILESYNC-001#ac-mirror and ac-layouts.</remarks>
    public Task<HostOutcome<MediaPairingResponse>> PairAsync(Guid libraryId, AuthenticatedHostInstance caller,
        MediaPairingRequest request, CancellationToken token) => OnLaneAsync<MediaPairingResponse>(async () =>
        {
            if (caller.LibraryId != libraryId || request.OriginInstanceId != caller.InstanceId)
                return new HostOutcome<MediaPairingResponse>.Forbidden();
            if (request.OperationId == Guid.Empty || request.PeerId == Guid.Empty || !Enum.IsDefined(request.Kind) ||
                string.IsNullOrWhiteSpace(request.DestinationRoot) || request.ExpectedRevision < 0)
                return new HostOutcome<MediaPairingResponse>.Rejected("invalid-media-pairing");

            var proposed = await store.ReservePairingAsync(libraryId, caller, request, token).ConfigureAwait(false);
            if (proposed is not HostOutcome<MediaPairingPreparation>.Success { Value: var preparation })
                return proposed.PropagateFailure<MediaPairingResponse>();
            if (preparation.Replay is { } replay)
                return new HostOutcome<MediaPairingResponse>.Success(replay);

            var result = await offers.OfferAsync(preparation.Target, token).ConfigureAwait(false);
            return result is HostOutcome<MediaShareOffer>.Success { Value: var offer }
                ? await store.CompletePairingAsync(libraryId, caller, request, preparation.Target, offer, token).ConfigureAwait(false)
                : result.PropagateFailure<MediaPairingResponse>();
        }, token);

    /// <summary>Refreshes verified status for registered mirrors and paired Android folders.</summary>
    /// <remarks>2026-11-24: Report actual receiver acceptance for FR-FILESYNC-001#ac-layouts without changing durable pairing receipts.</remarks>
    public Task<HostOutcome<MediaSyncSnapshot>> ReadAsync(Guid libraryId, AuthenticatedHostInstance caller, CancellationToken token) =>
        OnLaneAsync<MediaSyncSnapshot>(async () =>
        {
            var read = await store.ReadAsync(libraryId, caller, token).ConfigureAwait(false);
            if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var directory })
                return read.PropagateFailure<MediaSyncSnapshot>();
            var pendingAndroid = Task.WhenAll(directory.Android.Where(entry => entry.Target.ReceiverRoot is not null).Select(async entry =>
                (entry.Target, AndroidShareObservation.From(await androidShares.ReadShareStatusAsync(entry.Target, token).ConfigureAwait(false)))));
            var pendingMirrors = Task.WhenAll(directory.Mirrors.Select(async mirror =>
            {
                var previous = directory.Find(mirror)?.State;
                var observed = await native.ApplyAsync(mirror.Native, PcMirrorAction.Observe, previous, token).ConfigureAwait(false);
                return (mirror, ProjectObservation(observed, previous));
            }));
            await Task.WhenAll(pendingAndroid, pendingMirrors).ConfigureAwait(false);
            var recorded = await store.ObserveAsync(libraryId, caller, await pendingMirrors.ConfigureAwait(false),
                await pendingAndroid.ConfigureAwait(false), token).ConfigureAwait(false);
            return recorded is HostOutcome<MediaMirrorDirectory>.Success { Value: var updated }
                ? new HostOutcome<MediaSyncSnapshot>.Success(updated.Project(arrivals))
                : recorded.PropagateFailure<MediaSyncSnapshot>();
        }, token);

    /// <summary>Runs the exact authorized command or recovers its existing receipt after interruption.</summary>
    public Task<HostOutcome<MediaMirrorControlResponse>> ControlAsync(Guid libraryId, AuthenticatedHostInstance caller,
        MediaMirrorControlRequest request, CancellationToken token) => OnLaneAsync<MediaMirrorControlResponse>(async () =>
        {
            if (caller.LibraryId != libraryId || request.OriginInstanceId != caller.InstanceId)
                return new HostOutcome<MediaMirrorControlResponse>.Forbidden();
            if (request.OperationId == Guid.Empty || request.ShareId == Guid.Empty || !Enum.IsDefined(request.Action) ||
                (request.Action == MediaMirrorAction.Rescan ? request.ExpectedRevision is not null : request.ExpectedRevision is not > 0))
                return new HostOutcome<MediaMirrorControlResponse>.Rejected("invalid-mirror-control");

            var read = await store.ReadAsync(libraryId, caller, token).ConfigureAwait(false);
            if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var current })
                return read.PropagateFailure<MediaMirrorControlResponse>();
            if (current.Mirrors.FirstOrDefault(candidate => candidate.ShareId == request.ShareId) is not { } mirror)
                return new HostOutcome<MediaMirrorControlResponse>.Rejected("mirror-share-unavailable");
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { LibraryId = libraryId,
                MirrorScope = mirror.Scope, Request = request, Version = 1 })));
            var known = await store.ReadReceiptAsync(libraryId, caller, mirror, request, fingerprint, token).ConfigureAwait(false);
            if (known is not HostOutcome<PcMirrorReceipt?>.Success { Value: var receipt })
                return known.PropagateFailure<MediaMirrorControlResponse>();
            if (receipt?.Completed is { } replay)
                return new HostOutcome<MediaMirrorControlResponse>.Success(replay);

            var previous = current.Find(mirror)?.State;
            if (receipt is null)
            {
                var fresh = await native.ApplyAsync(mirror.Native, PcMirrorAction.Observe, previous, token).ConfigureAwait(false);
                if (fresh is not HostOutcome<PcMirrorNativeState>.Success { Value: var observed })
                    return fresh.PropagateFailure<MediaMirrorControlResponse>();
                if (observed.Phase == PcMirrorPhase.AwaitingAcceptance)
                    return new HostOutcome<MediaMirrorControlResponse>.Rejected("mirror-share-not-accepted");
                var synchronized = await store.ObserveAsync(libraryId, caller, [(mirror, observed)], [], token).ConfigureAwait(false);
                if (synchronized is not HostOutcome<MediaMirrorDirectory>.Success)
                    return synchronized.PropagateFailure<MediaMirrorControlResponse>();
                previous = observed;
            }
            var reserved = await store.ReserveAsync(libraryId, caller, mirror, request, fingerprint, token).ConfigureAwait(false);
            if (reserved is not HostOutcome<PcMirrorReceipt>.Success { Value: var reservation })
                return reserved.PropagateFailure<MediaMirrorControlResponse>();
            if (reservation.Completed is { } completed)
                return new HostOutcome<MediaMirrorControlResponse>.Success(completed);

            var action = request.Action switch
            {
                MediaMirrorAction.Pause => PcMirrorAction.Pause,
                MediaMirrorAction.Resume => PcMirrorAction.Resume,
                _ => PcMirrorAction.Rescan
            };
            var applied = await native.ApplyAsync(mirror.Native, action, previous, token).ConfigureAwait(false);
            return applied is HostOutcome<PcMirrorNativeState>.Success { Value: var state }
                ? await store.CompleteAsync(libraryId, caller, mirror, request, fingerprint, state, token).ConfigureAwait(false)
                : applied.PropagateFailure<MediaMirrorControlResponse>();
        }, token);

    private static PcMirrorNativeState ProjectObservation(HostOutcome<PcMirrorNativeState> result, PcMirrorNativeState? prior) => result switch
    {
        HostOutcome<PcMirrorNativeState>.Success success => success.Value,
        HostOutcome<PcMirrorNativeState>.Rejected failure => Unavailable(failure.Code, prior),
        HostOutcome<PcMirrorNativeState>.Unavailable failure => Unavailable(failure.Code, prior),
        _ => Unavailable("mirror-observation-unavailable", prior)
    };

    private static PcMirrorNativeState Unavailable(string code, PcMirrorNativeState? prior) =>
        new(false, prior?.Paused == true, PcMirrorPhase.Failed, prior?.FilesReceived, prior?.TotalFiles, code);

    private async Task<HostOutcome<T>> OnLaneAsync<T>(Func<Task<HostOutcome<T>>> execute, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await execute().ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<T>.Cancelled();
        }
    }

    public void Dispose() => lane.Dispose();
}
