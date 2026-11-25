using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists registered mirror observations, revision fences, and replayable control outcomes.</summary>
/// <remarks>2026-11-24: Bind durable STB10 pause, resume and rescan to FR-FILESYNC-001#ac-mirror.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string PcMirrorStateCollection = "pc_mirror_states";
    private const string MirrorControlPending = "PcMirrorControlPending";
    private const string MirrorControlCompleted = "PcMirrorControlCompleted";

    /// <summary>Connects the host-local routes to the existing database lane and operation receipts.</summary>
    internal IPcMirrorStore CreatePcMirrorStore(LibraryHostOptions options, IEnumerable<AndroidLayoutProfile> profiles, AndroidArrivalHub arrivals) =>
        new ConfiguredPcMirrorStore(this, options, profiles, arrivals);

    private sealed partial class ConfiguredPcMirrorStore(LiteDbHostPlaylistStore store, LibraryHostOptions options,
        IEnumerable<AndroidLayoutProfile> profiles, AndroidArrivalHub arrivals) : IPcMirrorStore
    {
        public Task<HostOutcome<MediaMirrorDirectory>> ReadAsync(Guid libraryId, AuthenticatedHostInstance caller, CancellationToken token) =>
            store.OnLaneAsync(() => ReadDirectory(libraryId, caller), token);

        /// <inheritdoc />
        /// <remarks>2026-11-24: Advance the media directory only when verified status changes for FR-FILESYNC-001#ac-layouts.</remarks>
        public Task<HostOutcome<MediaMirrorDirectory>> ObserveAsync(Guid libraryId, AuthenticatedHostInstance caller,
            IReadOnlyList<(PcMirrorBinding Mirror, PcMirrorNativeState State)> observations,
            IReadOnlyList<(RegisteredAndroidExport Target, AndroidShareObservation State)> android, CancellationToken token) =>
            store.ExecuteAsync<MediaMirrorDirectory>(_ =>
            {
                var read = ReadDirectory(libraryId, caller);
                if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var known })
                    return read;
                var entries = known.Observations.ToBuilder();
                var changed = false;
                foreach (var (mirror, state) in observations)
                {
                    if (!ContainsBinding(known, mirror))
                        continue;
                    var previous = known.Find(mirror);
                    if (previous?.State == state)
                        continue;
                    entries[mirror.ShareId] = Update(previous, mirror, state);
                    changed = true;
                }
                var peers = known.Android.ToArray();
                var instances = store.database.GetCollection<BsonDocument>("registered_instances");
                foreach (var (target, state) in android)
                {
                    var index = Array.FindIndex(peers, entry => entry.Target == target);
                    if (index < 0 || peers[index].Observation == state)
                        continue;
                    var row = instances.FindById(target.DeviceId);
                    var export = row["AndroidExport"].AsDocument;
                    export["ReceiverSharePhase"] = state.Phase.ToString();
                    if (state.FailureCode is { } code)
                        export["ReceiverFailureCode"] = code;
                    else
                        export.Remove("ReceiverFailureCode");
                    instances.Update(row);
                    peers[index] = (target, peers[index].Name, state);
                    changed = true;
                }
                return new HostOutcome<MediaMirrorDirectory>.Success(changed
                    ? Save(libraryId, known with { Android = [.. peers] }, entries.ToImmutable()) : known);
            }, token);

        public Task<HostOutcome<PcMirrorReceipt?>> ReadReceiptAsync(Guid libraryId, AuthenticatedHostInstance caller, PcMirrorBinding mirror,
            MediaMirrorControlRequest request, string fingerprint, CancellationToken token) =>
            store.OnLaneAsync<PcMirrorReceipt?>(() =>
            {
                var read = ReadDirectory(libraryId, caller);
                return read is HostOutcome<MediaMirrorDirectory>.Success { Value: var directory }
                    ? ReadReceipt(directory, mirror, OperationId(new(caller.InstanceId, request.OperationId)), fingerprint)
                    : read.PropagateFailure<PcMirrorReceipt?>();
            }, token);

        public Task<HostOutcome<PcMirrorReceipt>> ReserveAsync(Guid libraryId, AuthenticatedHostInstance caller, PcMirrorBinding mirror,
            MediaMirrorControlRequest request, string fingerprint, CancellationToken token) => store.ExecuteAsync<PcMirrorReceipt>(_ =>
            {
                var read = ReadDirectory(libraryId, caller);
                if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var directory })
                    return read.PropagateFailure<PcMirrorReceipt>();
                var key = OperationId(new(caller.InstanceId, request.OperationId));
                var prior = ReadReceipt(directory, mirror, key, fingerprint);
                if (prior is not HostOutcome<PcMirrorReceipt?>.Success { Value: var receipt })
                    return prior.PropagateFailure<PcMirrorReceipt>();
                if (receipt is not null)
                    return new HostOutcome<PcMirrorReceipt>.Success(receipt);
                var current = directory.Find(mirror);
                if (current is null)
                    return new HostOutcome<PcMirrorReceipt>.Rejected("mirror-share-unavailable");
                if (request.ExpectedRevision is { } expected && expected != current.Revision)
                    return new HostOutcome<PcMirrorReceipt>.Rejected("mirror-revision-changed", current.Revision, Conflict: true);
                if (request.Action == MediaMirrorAction.Rescan && current.State.Paused)
                    return new HostOutcome<PcMirrorReceipt>.Rejected("mirror-share-paused");

                Save(libraryId, directory, directory.Observations.SetItem(mirror.ShareId, current with
                {
                    Revision = checked(current.Revision + 1),
                    PendingOperation = key
                }));
                store.database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
                {
                    ["_id"] = key, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId,
                    ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument { ["Type"] = MirrorControlPending, ["Version"] = 1 }
                });
                return new HostOutcome<PcMirrorReceipt>.Success(new(null));
            }, token);

        public Task<HostOutcome<MediaMirrorControlResponse>> CompleteAsync(Guid libraryId, AuthenticatedHostInstance caller,
            PcMirrorBinding mirror, MediaMirrorControlRequest request, string fingerprint, PcMirrorNativeState observation, CancellationToken token) =>
            store.ExecuteAsync<MediaMirrorControlResponse>(_ =>
            {
                var read = ReadDirectory(libraryId, caller);
                if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var directory })
                    return read.PropagateFailure<MediaMirrorControlResponse>();
                var key = OperationId(new(caller.InstanceId, request.OperationId));
                var stored = ReadReceipt(directory, mirror, key, fingerprint);
                if (stored is not HostOutcome<PcMirrorReceipt?>.Success { Value: var receipt })
                    return stored.PropagateFailure<MediaMirrorControlResponse>();
                if (receipt?.Completed is { } replay)
                    return new HostOutcome<MediaMirrorControlResponse>.Success(replay);
                if (receipt is null || directory.Find(mirror) is not { } current)
                    return new HostOutcome<MediaMirrorControlResponse>.Rejected("mirror-operation-unavailable");

                var verified = Update(current, mirror, observation) with { PendingOperation = null };
                var final = Save(libraryId, directory, directory.Observations.SetItem(mirror.ShareId, verified));
                var response = new MediaMirrorControlResponse(request.OperationId, mirror.ShareId, final.Project(arrivals));
                var table = store.database.GetCollection<BsonDocument>("operation_receipts");
                var row = table.FindById(key);
                row["Receipt"] = new BsonDocument { ["Type"] = MirrorControlCompleted, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(response) };
                table.Update(row);
                return new HostOutcome<MediaMirrorControlResponse>.Success(response);
            }, token);

        private HostOutcome<MediaMirrorDirectory> ReadDirectory(Guid libraryId, AuthenticatedHostInstance caller)
        {
            var instances = store.database.GetCollection<BsonDocument>("registered_instances");
            if (!store.IsPublicationCaller(libraryId, caller) || Text(instances.FindById(caller.InstanceId), "Role") != "Desktop")
                return new HostOutcome<MediaMirrorDirectory>.Forbidden();

            var mirrors = ImmutableArray.CreateBuilder<PcMirrorBinding>();
            foreach (var route in options.PcMirrors)
            {
                var row = instances.FindById(route.PeerId);
                if (row is null || !row["LibraryId"].IsGuid || row["LibraryId"].AsGuid != libraryId || Text(row, "Status") != "Active" ||
                    Text(row, "Role") != "Desktop" || Text(row, "SyncthingDeviceId") is not { Length: > 0 } receiverId)
                    continue;
                var native = new RegisteredPcMirror(libraryId, route.PeerId, route.FolderId, route.ReceiverEndpoint, receiverId, route.ReceiverRoot,
                    route.SenderEndpoint, route.SenderDeviceId, options.MusicRoot);
                mirrors.Add(new(PeerName(row, route.PeerId), native));
            }
            var android = ImmutableArray.CreateBuilder<(RegisteredAndroidExport, string, AndroidShareObservation)>();
            foreach (var row in instances.Find(Query.EQ("LibraryId", libraryId)).Where(row => Text(row, "Role") == "Android"))
            {
                if (row["_id"].IsGuid && store.ReadRegisteredAndroidExport(libraryId, row["_id"].AsGuid) is { } registered)
                {
                    android.Add((registered, PeerName(row, registered.DeviceId), ReadAndroidObservation(row["AndroidExport"].AsDocument)));
                }
            }

            var status = store.database.GetCollection<BsonDocument>(PcMirrorStateCollection).FindById(libraryId);
            var known = status is null ? ImmutableDictionary<Guid, StoredPcMirror>.Empty :
                ReadStored<ImmutableDictionary<Guid, StoredPcMirror>>(status["Observations"]);
            return new HostOutcome<MediaMirrorDirectory>.Success(new(status?["Revision"].AsInt64 ?? 0, known, mirrors.ToImmutable(),
                [.. android.OrderBy(peer => peer.Item1.DeviceId)]));
        }

        private MediaMirrorDirectory Save(Guid libraryId, MediaMirrorDirectory directory, ImmutableDictionary<Guid, StoredPcMirror> observations)
        {
            var revision = checked(directory.Revision + 1);
            store.database.GetCollection<BsonDocument>(PcMirrorStateCollection).Upsert(new BsonDocument
            {
                ["_id"] = libraryId, ["Revision"] = revision, ["Observations"] = JsonSerializer.Serialize(observations)
            });
            return directory with { Revision = revision, Observations = observations };
        }

        private HostOutcome<PcMirrorReceipt?> ReadReceipt(MediaMirrorDirectory directory, PcMirrorBinding mirror, string key, string fingerprint)
        {
            if (!ContainsBinding(directory, mirror))
                return new HostOutcome<PcMirrorReceipt?>.Rejected("mirror-share-unavailable", Conflict: true);
            var row = store.database.GetCollection<BsonDocument>("operation_receipts").FindById(key);
            if (row is null)
                return new HostOutcome<PcMirrorReceipt?>.Success(null);
            var receipt = row["Receipt"].AsDocument;
            if (row["IntentFingerprint"].AsString != fingerprint || receipt["Version"].AsInt32 != 1)
                return new HostOutcome<PcMirrorReceipt?>.Rejected("operation-intent-mismatch", Conflict: true);
            return receipt["Type"].AsString switch
            {
                MirrorControlCompleted => new HostOutcome<PcMirrorReceipt?>.Success(new(ReadStored<MediaMirrorControlResponse>(receipt["Result"]))),
                MirrorControlPending when directory.Find(mirror)?.PendingOperation == key => new HostOutcome<PcMirrorReceipt?>.Success(new(null)),
                MirrorControlPending => new HostOutcome<PcMirrorReceipt?>.Rejected("mirror-operation-superseded", directory.Find(mirror)?.Revision, Conflict: true),
                _ => new HostOutcome<PcMirrorReceipt?>.Rejected("operation-intent-mismatch", Conflict: true)
            };
        }

        private static StoredPcMirror Update(StoredPcMirror? old, PcMirrorBinding mirror, PcMirrorNativeState state) => new(mirror.Scope,
            old is null ? 1 : old.State.Paused == state.Paused ? old.Revision : checked(old.Revision + 1),
            checked((old?.ConnectionGeneration ?? 0) + (state.Connected && old?.State.Connected != true ? 1 : 0)),
            state, old?.PendingOperation);

        private static bool ContainsBinding(MediaMirrorDirectory directory, PcMirrorBinding mirror) =>
            directory.Mirrors.Any(candidate => candidate.ShareId == mirror.ShareId && candidate.Native == mirror.Native);

        private static string PeerName(BsonDocument row, Guid id) => Text(row, "Name") is { Length: > 0 } name ? name : id.ToString("D");

        /// <summary>Falls back to the original offer decision before the first current observation.</summary>
        private static AndroidShareObservation ReadAndroidObservation(BsonDocument export)
        {
            if (Enum.TryParse<MediaSharePhase>(Text(export, "ReceiverSharePhase"), out var phase) && phase is
                MediaSharePhase.AwaitingAcceptance or MediaSharePhase.Ready or MediaSharePhase.Paused or MediaSharePhase.Failed or MediaSharePhase.Unknown)
                return new(phase, phase == MediaSharePhase.Failed ? Text(export, "ReceiverFailureCode") : null);
            var accepted = export["ReceiverAcceptedAtOffer"];
            return new(accepted.IsBoolean && !accepted.AsBoolean ? MediaSharePhase.AwaitingAcceptance : MediaSharePhase.Unknown);
        }
    }
}
