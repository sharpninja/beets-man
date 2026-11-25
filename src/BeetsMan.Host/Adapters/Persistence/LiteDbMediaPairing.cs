using System.Security.Cryptography;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Stores authorized pairing intents and the verified registration binding.</summary>
/// <remarks>2026-11-24: Recover STB10 pairing approvals for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private sealed partial class ConfiguredPcMirrorStore
    {
        private const string PairingPending = "MediaPairingPending";
        private const string PairingCompleted = "MediaPairingCompleted";
        private readonly Dictionary<string, AndroidLayoutProfile> layouts = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<HostOutcome<MediaPairingPreparation>> ReservePairingAsync(Guid libraryId, AuthenticatedHostInstance caller,
            MediaPairingRequest request, CancellationToken token) => store.ExecuteAsync<MediaPairingPreparation>(_ =>
            {
                var resolved = ResolvePairing(libraryId, caller, request);
                if (resolved is not HostOutcome<PairingScope>.Success { Value: var scope })
                    return resolved.PropagateFailure<MediaPairingPreparation>();

                var known = ReadPairingReceipt(scope);
                if (known is not HostOutcome<PairingReceipt?>.Success { Value: var receipt })
                    return known.PropagateFailure<MediaPairingPreparation>();
                if (receipt is not null)
                    return new HostOutcome<MediaPairingPreparation>.Success(new(scope.Target, receipt.Response));
                if (scope.Directory.Revision != request.ExpectedRevision)
                    return new HostOutcome<MediaPairingPreparation>.Rejected("pairing-revision-changed", scope.Directory.Revision, Conflict: true);
                if (scope.Layout is { } layout && store.ReadRegisteredAndroidExport(libraryId, request.PeerId) is { } current &&
                    store.ReadExportAssignment(current).Profile is { } active && (active.ProfileId != layout.Id || active.Revision != layout.Version))
                    return new HostOutcome<MediaPairingPreparation>.Rejected("pairing-layout-already-assigned", active.Revision, Conflict: true);

                scope.Peer["PendingMediaPairing"] = scope.Key;
                store.database.GetCollection<BsonDocument>("registered_instances").Update(scope.Peer);
                store.database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
                {
                    ["_id"] = scope.Key, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId,
                    ["IntentFingerprint"] = scope.Intent, ["Receipt"] = new BsonDocument
                    {
                        ["Type"] = PairingPending, ["Version"] = 1, ["PairingRequestHash"] = scope.RequestHash
                    }
                });
                Save(libraryId, scope.Directory, scope.Directory.Observations);
                return new HostOutcome<MediaPairingPreparation>.Success(new(scope.Target, null));
            }, token);

        /// <inheritdoc />
        public Task<HostOutcome<MediaPairingResponse>> CompletePairingAsync(Guid libraryId, AuthenticatedHostInstance caller,
            MediaPairingRequest request, RegisteredMediaPairing target, MediaShareOffer offer, CancellationToken token) =>
            store.ExecuteAsync<MediaPairingResponse>(_ =>
            {
                var resolved = ResolvePairing(libraryId, caller, request);
                if (resolved is not HostOutcome<PairingScope>.Success { Value: var scope })
                    return resolved.PropagateFailure<MediaPairingResponse>();
                var known = ReadPairingReceipt(scope);
                if (known is not HostOutcome<PairingReceipt?>.Success { Value: var receipt })
                    return known.PropagateFailure<MediaPairingResponse>();
                if (receipt?.Response is { } replay)
                    return new HostOutcome<MediaPairingResponse>.Success(replay);
                if (receipt is null || scope.Target != target)
                    return new HostOutcome<MediaPairingResponse>.Rejected("pairing-scope-changed", Conflict: true);

                var directory = scope.Directory;
                var observations = directory.Observations;
                if (scope.Layout is { } layout)
                {
                    var export = scope.Peer["AndroidExport"].IsDocument ? scope.Peer["AndroidExport"].AsDocument : new BsonDocument
                    {
                        ["Revision"] = 1L, ["ShareId"] = target.FolderId, ["ReceiverEndpoint"] = target.ReceiverEndpoint.AbsoluteUri,
                        ["CaseSensitivePaths"] = scope.AndroidRoute!.CaseSensitivePaths
                    };
                    export["InitialProfileId"] = layout.Id;
                    export["InitialProfileRevision"] = layout.Version;
                    export["ReceiverRoot"] = target.ReceiverRoot;
                    export["ApprovedSenderId"] = target.SenderDeviceId;
                    export["ReceiverAcceptedAtOffer"] = offer.ReceiverAccepted;
                    export.Remove("ReceiverSharePhase");
                    export.Remove("ReceiverFailureCode");
                    scope.Peer["AndroidExport"] = export;
                }
                else if (!offer.ReceiverAccepted && scope.Mirror is { } mirror)
                {
                    var awaiting = new PcMirrorNativeState(false, false, PcMirrorPhase.AwaitingAcceptance, null, null);
                    observations = observations.SetItem(mirror.ShareId, Update(directory.Find(mirror), mirror, awaiting));
                }
                scope.Peer.Remove("PendingMediaPairing");
                store.database.GetCollection<BsonDocument>("registered_instances").Update(scope.Peer);
                Save(libraryId, directory, observations);

                var refreshed = ReadDirectory(libraryId, caller);
                if (refreshed is not HostOutcome<MediaMirrorDirectory>.Success { Value: var final })
                    return refreshed.PropagateFailure<MediaPairingResponse>();
                var response = new MediaPairingResponse(request.OperationId, request.PeerId, final.Project(arrivals));
                var receipts = store.database.GetCollection<BsonDocument>("operation_receipts");
                var row = scope.StoredReceipt!;
                var stored = row["Receipt"].AsDocument;
                stored["Type"] = PairingCompleted;
                stored["Result"] = JsonSerializer.Serialize(response);
                receipts.Update(row);
                return new HostOutcome<MediaPairingResponse>.Success(response);
            }, token);

        /// <summary>Resolves exact routes using the same registration rules as the media directory.</summary>
        private HostOutcome<PairingScope> ResolvePairing(Guid libraryId, AuthenticatedHostInstance caller, MediaPairingRequest request)
        {
            var read = ReadDirectory(libraryId, caller);
            if (read is not HostOutcome<MediaMirrorDirectory>.Success { Value: var directory })
                return read.PropagateFailure<PairingScope>();
            var peer = store.database.GetCollection<BsonDocument>("registered_instances").FindById(request.PeerId);
            if (peer is null || !peer["LibraryId"].IsGuid || peer["LibraryId"].AsGuid != libraryId || Text(peer, "Status") != "Active" ||
                Text(peer, "SyncthingDeviceId") is not { Length: > 0 } receiverId)
                return new HostOutcome<PairingScope>.Rejected("pairing-peer-unavailable");

            var key = OperationId(new(caller.InstanceId, request.OperationId));
            var prior = store.database.GetCollection<BsonDocument>("operation_receipts").FindById(key);
            var requestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { LibraryId = libraryId, Request = request })));
            if (prior is not null && (!prior["Receipt"].IsDocument || Text(prior["Receipt"].AsDocument, "PairingRequestHash") != requestHash))
                return new HostOutcome<PairingScope>.Rejected("operation-intent-mismatch", Conflict: true);

            RegisteredMediaPairing target;
            AndroidLayoutProfile? layout = null;
            AndroidHostExport? route = null;
            PcMirrorBinding? mirror = null;
            bool? caseSensitivePaths = null;
            if (request.Kind == MediaPeerType.PcMirror)
            {
                mirror = directory.Mirrors.FirstOrDefault(candidate => candidate.Native.PeerId == request.PeerId);
                if (mirror is null || request.ProfileId is not null)
                    return new HostOutcome<PairingScope>.Rejected("pairing-mirror-unavailable");
                var native = mirror.Native;
                target = new(libraryId, request.PeerId, mirror.Name, request.Kind, native.FolderId, native.SenderEndpoint, native.SenderDeviceId,
                    native.MusicRoot, native.ReceiverEndpoint, native.ReceiverDeviceId, native.ReceiverRoot);
            }
            else
            {
                route = options.AndroidExports.FirstOrDefault(candidate => candidate.DeviceId == request.PeerId);
                if (Text(peer, "Role") != "Android" || route is null)
                    return new HostOutcome<PairingScope>.Rejected("pairing-android-unavailable");
                if (!layouts.TryGetValue(request.ProfileId ?? AndroidLayoutProfile.DefaultId, out layout))
                    return new HostOutcome<PairingScope>.Rejected("pairing-layout-unavailable");
                var existing = store.ReadRegisteredAndroidExport(libraryId, request.PeerId);
                var receiver = route.ReceiverEndpoint ?? existing?.ReceiverEndpoint;
                var receiverRoot = route.ReceiverRoot ?? layout.SuggestedRoot;
                if (receiver is null || !SyncthingReceiverInventory.IsTrustedEndpoint(receiver) || receiver == route.Sender.Endpoint ||
                    peer["AndroidExport"].IsDocument && existing is null || existing is not null &&
                    (existing.ShareId != route.FolderId || existing.ReceiverEndpoint != receiver ||
                        existing.ReceiverRoot is { } previousRoot && !PcMirrorNativePath.Matches(receiverRoot, previousRoot) ||
                        existing.ApprovedSenderId is { } previousSender && previousSender != route.Sender.DeviceId))
                    return new HostOutcome<PairingScope>.Rejected("pairing-android-route-unavailable");
                caseSensitivePaths = existing?.CaseSensitivePaths ?? route.CaseSensitivePaths;
                target = new(libraryId, request.PeerId, PeerName(peer, request.PeerId), request.Kind, route.FolderId, route.Sender.Endpoint,
                    route.Sender.DeviceId, route.Sender.ExportRoot, receiver, receiverId, receiverRoot);
            }
            if (!PcMirrorNativePath.Matches(request.DestinationRoot, target.ReceiverRoot))
                return new HostOutcome<PairingScope>.Rejected("pairing-destination-unavailable");

            var intent = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 1, RequestHash = requestHash, Target = target with { Name = "" }, LayoutVersion = layout?.Version,
                CaseSensitivePaths = caseSensitivePaths
            })));
            return new HostOutcome<PairingScope>.Success(new(directory, peer, target, layout, route, mirror, key, intent, requestHash, prior));
        }

        /// <summary>Loads the exact response or proves that the operation still owns the pending target.</summary>
        private static HostOutcome<PairingReceipt?> ReadPairingReceipt(PairingScope scope)
        {
            if (scope.StoredReceipt is not { } row)
                return new HostOutcome<PairingReceipt?>.Success(null);
            var receipt = row["Receipt"].AsDocument;
            if (Text(row, "IntentFingerprint") != scope.Intent || receipt["Version"].AsInt32 != 1)
                return new HostOutcome<PairingReceipt?>.Rejected("operation-intent-mismatch", Conflict: true);
            return Text(receipt, "Type") switch
            {
                PairingCompleted => new HostOutcome<PairingReceipt?>.Success(new(ReadStored<MediaPairingResponse>(receipt["Result"]))),
                PairingPending when Text(scope.Peer, "PendingMediaPairing") == scope.Key => new HostOutcome<PairingReceipt?>.Success(new(null)),
                PairingPending => new HostOutcome<PairingReceipt?>.Rejected("pairing-operation-superseded", scope.Directory.Revision, Conflict: true),
                _ => new HostOutcome<PairingReceipt?>.Rejected("operation-intent-mismatch", Conflict: true)
            };
        }

        /// <summary>Captures the resolved route, registered row and operation identity in the database transaction.</summary>
        private sealed record PairingScope(MediaMirrorDirectory Directory, BsonDocument Peer, RegisteredMediaPairing Target,
            AndroidLayoutProfile? Layout, AndroidHostExport? AndroidRoute, PcMirrorBinding? Mirror, string Key, string Intent, string RequestHash,
            BsonDocument? StoredReceipt);

        /// <summary>Distinguishes a pending matching receipt from an absent one.</summary>
        private sealed record PairingReceipt(MediaPairingResponse? Response);
    }
}
