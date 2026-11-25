using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Host.Application;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Stores the private preview identity with the staged plan, before publication is permitted.</summary>
/// <remarks>2026-11-24: Bind the digest, revision and retry receipt in one transaction for FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string PreviewReceiptType = "DeviceExportPreviewPrepared";

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportPreviewPreparation>> ReadPreviewSourcesAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPreviewIntent intent, CancellationToken token) => OnLaneAsync<DeviceExportPreviewPreparation>(() =>
        {
            var prior = ReadPreviewReceipt(libraryId, caller, intent);
            if (prior is not HostOutcome<string?>.Success { Value: var replay })
                return prior.PropagateFailure<DeviceExportPreviewPreparation>();
            if (replay is not null)
                return new HostOutcome<DeviceExportPreviewPreparation>.Success(new DeviceExportPreviewPreparation.Replay(replay));

            var read = ReadPreviewPlan(libraryId, caller, intent.PlanId, intent.ExpectedPlanRevision, intent);
            if (read is not HostOutcome<(BsonDocument Row, RegisteredAndroidExport Target)>.Success { Value: var current })
                return read.PropagateFailure<DeviceExportPreviewPreparation>();
            if (HasPendingPublication(current.Target))
                return new HostOutcome<DeviceExportPreviewPreparation>.Rejected("export-publication-in-progress", Conflict: true);
            return new HostOutcome<DeviceExportPreviewPreparation>.Success(new DeviceExportPreviewPreparation.New(
                new(ReadPlan(current.Row), current.Target, ReadPublishedManifest(current.Target), ReadPlaylistDigests(intent.PlanId))));
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<string>> SavePreviewAsync(Guid libraryId, AuthenticatedHostInstance caller, DeviceExportPreviewIntent intent,
        DeviceExportPreviewSources sources, string digest, CancellationToken token) => ExecuteAsync<string>(_ =>
        {
            var prior = ReadPreviewReceipt(libraryId, caller, intent);
            if (prior is not HostOutcome<string?>.Success { Value: var replay })
                return prior.PropagateFailure<string>();
            if (replay is not null)
                return new HostOutcome<string>.Success(replay);

            var read = ReadPreviewPlan(libraryId, caller, intent.PlanId, intent.ExpectedPlanRevision, intent);
            if (read is not HostOutcome<(BsonDocument Row, RegisteredAndroidExport Target)>.Success { Value: var current })
                return read.PropagateFailure<string>();
            if (current.Target != sources.Destination)
                return new HostOutcome<string>.Rejected("export-registration-changed", current.Target.Revision, Conflict: true);
            var manifestRevision = ManifestRevision(ManifestKey(current.Target));
            if (sources.Manifest.Revision != manifestRevision)
                return new HostOutcome<string>.Rejected("export-manifest-changed", manifestRevision, Conflict: true);
            if (HasPendingPublication(current.Target))
                return new HostOutcome<string>.Rejected("export-publication-in-progress", Conflict: true);

            current.Row["ManifestRevision"] = checked(intent.ExpectedPlanRevision + 1);
            current.Row["PreparedPreviewIdentity"] = digest;
            database.GetCollection<BsonDocument>(DeviceExportCollection).Update(current.Row);
            var operation = new HostOperationKey(caller.InstanceId, intent.OperationId);
            database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
            {
                ["_id"] = OperationId(operation), ["OriginInstanceId"] = operation.OriginInstanceId, ["OperationId"] = operation.OperationId,
                ["IntentFingerprint"] = PreviewFingerprint(libraryId, intent),
                ["Receipt"] = new BsonDocument { ["Type"] = PreviewReceiptType, ["Version"] = 1, ["Digest"] = digest }
            });
            return new HostOutcome<string>.Success(digest);
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<string>> ReadPreviewDigestAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid planId, long revision,
        CancellationToken token) => OnLaneAsync<string>(() =>
        {
            var read = ReadPreviewPlan(libraryId, caller, planId, revision);
            if (read is not HostOutcome<(BsonDocument Row, RegisteredAndroidExport Target)>.Success { Value: var current })
                return read.PropagateFailure<string>();
            return Text(current.Row, "PreparedPreviewIdentity") is { } digest ? new HostOutcome<string>.Success(digest) :
                new HostOutcome<string>.Rejected("export-preview-required");
        }, token);

    private HostOutcome<string?> ReadPreviewReceipt(Guid libraryId, AuthenticatedHostInstance caller, DeviceExportPreviewIntent intent)
    {
        if (!IsPublicationCaller(libraryId, caller))
            return new HostOutcome<string?>.Forbidden();
        if (intent.OperationId == Guid.Empty || intent.PlanId == Guid.Empty || intent.ExpectedPlanRevision is <= 0 or long.MaxValue ||
            intent.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(intent.ProfileId) || intent.ProfileRevision <= 0)
            return new HostOutcome<string?>.Rejected("invalid-export-preview-intent");
        var key = new HostOperationKey(caller.InstanceId, intent.OperationId);
        var row = database.GetCollection<BsonDocument>("operation_receipts").FindById(OperationId(key));
        if (row is null)
            return new HostOutcome<string?>.Success(null);
        var receipt = row["Receipt"].AsDocument;
        if (row["IntentFingerprint"].AsString != PreviewFingerprint(libraryId, intent) ||
            receipt["Type"].AsString != PreviewReceiptType || receipt["Version"].AsInt32 != 1)
            return new HostOutcome<string?>.Rejected("operation-intent-mismatch", Conflict: true);
        return new HostOutcome<string?>.Success(receipt["Digest"].AsString);
    }

    private HostOutcome<(BsonDocument Row, RegisteredAndroidExport Target)> ReadPreviewPlan(Guid libraryId, AuthenticatedHostInstance caller,
        Guid planId, long revision, DeviceExportPreviewIntent? selection = null)
    {
        if (!IsPublicationCaller(libraryId, caller))
            return new HostOutcome<(BsonDocument, RegisteredAndroidExport)>.Forbidden();
        var row = database.GetCollection<BsonDocument>(DeviceExportCollection).FindById(planId);
        if (row is null || row["LibraryId"].AsGuid != libraryId || selection is not null && selection.DeviceId != row["DeviceId"].AsGuid)
            return new HostOutcome<(BsonDocument, RegisteredAndroidExport)>.Rejected("export-plan-unavailable");
        var registered = ReadAuthorizedExport(libraryId, caller, row["DeviceId"].AsGuid);
        if (registered is not HostOutcome<RegisteredAndroidExport>.Success { Value: var target })
            return registered.PropagateFailure<(BsonDocument, RegisteredAndroidExport)>();
        var current = row["ManifestRevision"].AsInt64;
        if (selection is not null && CheckProfileSlot<(BsonDocument, RegisteredAndroidExport)>(target, planId, selection.ProfileId) is { } profileConflict)
            return profileConflict;
        if (row["ShareId"].AsString != target.ShareId || row["CaseSensitivePaths"].AsBoolean != target.CaseSensitivePaths)
            return new HostOutcome<(BsonDocument, RegisteredAndroidExport)>.Rejected("export-share-changed", current, Conflict: true);
        return revision == current ? new HostOutcome<(BsonDocument, RegisteredAndroidExport)>.Success((row, target)) :
            new HostOutcome<(BsonDocument, RegisteredAndroidExport)>.Rejected("export-plan-changed", current, Conflict: true);
    }

    private HostOutcome<T>? CheckProfileSlot<T>(RegisteredAndroidExport target, Guid planId, string nextProfile)
    {
        var occupant = FindPlanRow(target.LibraryId, target.DeviceId, nextProfile);
        return occupant is not null && occupant["_id"].AsGuid != planId
            ? new HostOutcome<T>.Rejected("export-profile-plan-exists", occupant["ManifestRevision"].AsInt64, Conflict: true)
            : null;
    }

    private static string PreviewFingerprint(Guid libraryId, DeviceExportPreviewIntent intent)
    {
        var scope = intent with { RelativePathsByTrack = null };
        var identity = intent.RelativePathsByTrack is { Count: > 0 } paths
            ? JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, Type = PreviewReceiptType, LibraryId = libraryId, Intent = scope,
                Edits = paths.OrderBy(pair => pair.Key) })
            : JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Type = PreviewReceiptType, LibraryId = libraryId, Intent = scope });
        return Convert.ToHexString(SHA256.HashData(identity));
    }
}
