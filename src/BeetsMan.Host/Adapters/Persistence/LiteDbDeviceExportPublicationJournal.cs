using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Application;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the planned file actions and the last committed ownership on the host database lane.</summary>
/// <remarks>2026-11-24: Recover publication and change only the registered export manifest for FR-FILESYNC-001#ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string ExportManifestCollection = "device_export_manifests";
    private const string ExportOwnedFileCollection = "device_export_owned_files";
    private const string ExportPublicationJournal = "device_export_publication_journal";
    private const string ExportPublicationItems = "device_export_publication_items";
    private const string PendingPublicationReceipt = "DeviceExportPublicationPending";
    private const string CompletedPublicationReceipt = "DeviceExportPublished";

    /// <inheritdoc />
    public Task<HostOutcome<PublishedDeviceExportManifest>> ReadPublishedManifestAsync(Guid libraryId, AuthenticatedHostInstance caller,
        Guid deviceId, CancellationToken token) => OnLaneAsync(() =>
        {
            var registered = ReadAuthorizedExport(libraryId, caller, deviceId);
            if (registered is not HostOutcome<RegisteredAndroidExport>.Success { Value: var target })
                return registered.PropagateFailure<PublishedDeviceExportManifest>();
            return new HostOutcome<PublishedDeviceExportManifest>.Success(ReadPublishedManifest(target));
        }, token);

    private PublishedDeviceExportManifest ReadPublishedManifest(RegisteredAndroidExport target)
    {
        var key = ManifestKey(target);
        var files = database.GetCollection<BsonDocument>(ExportOwnedFileCollection).Find(Query.EQ("ManifestKey", key))
            .Select(ReadOwnedFile).OrderBy(file => file.Path, StringComparer.Ordinal);
        return new(target.LibraryId, target.DeviceId, target.ShareId, ManifestRevision(key), [.. files]);
    }

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportPublicationReservation?>> FindPublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, CancellationToken token) => OnLaneAsync(() => ReadPublicationReservation(libraryId, caller, intent), token);

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportPublicationReservation>> ReservePublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, PreparedDeviceExportPublication preview, CancellationToken token) => ExecuteAsync<DeviceExportPublicationReservation>(_ =>
        {
            var prior = ReadPublicationReservation(libraryId, caller, intent);
            if (prior is not HostOutcome<DeviceExportPublicationReservation?>.Success { Value: var existing })
                return prior.PropagateFailure<DeviceExportPublicationReservation>();
            if (existing is not null)
                return new HostOutcome<DeviceExportPublicationReservation>.Success(existing);

            var target = preview.Destination;
            if (target.LibraryId != libraryId || preview.Plan.DeviceId != target.DeviceId ||
                preview.Plan.PlanId != intent.PlanId || preview.Plan.PlanRevision != intent.PlanRevision ||
                preview.Operations.IsDefault || preview.NextOwnedFiles.IsDefault || preview.Issues.IsDefault || preview.LayoutEntries.IsDefault)
                return new HostOutcome<DeviceExportPublicationReservation>.Rejected("invalid-export-publication-scope");
            var previous = preview.PreviousProfile;
            var sourceProfile = previous ?? new(preview.Plan.ProfileId, preview.Plan.ProfileRevision);
            var current = CheckPublicationInputs<DeviceExportPublicationReservation>(target, intent.PlanId, intent.PlanRevision, preview.ManifestRevision,
                sourceProfile.Id, sourceProfile.Revision);
            if (current is not null)
                return current;
            if (previous is not null && CheckProfileSlot<DeviceExportPublicationReservation>(target, intent.PlanId, preview.Plan.ProfileId) is { } conflict)
                return conflict;

            var approved = DeviceExportPublicationPlanner.CheckApproval(preview, intent.Approval);
            if (approved is not HostOutcome<DeviceExportRemovalProposal?>.Success { Value: var proposal })
                return approved.PropagateFailure<DeviceExportPublicationReservation>();
            if (proposal is not null)
                return new HostOutcome<DeviceExportPublicationReservation>.Success(new DeviceExportPublicationReservation.ConfirmationRequired(proposal));

            var manifestKey = ManifestKey(target);
            if (HasPendingPublication(target))
                return new HostOutcome<DeviceExportPublicationReservation>.Rejected("export-publication-in-progress", Conflict: true);
            if (HasLeasedScan(manifestKey, DateTime.UtcNow))
                return new HostOutcome<DeviceExportPublicationReservation>.Unavailable("export-scan-in-progress");

            var publication = Guid.NewGuid();
            var key = new HostOperationKey(caller.InstanceId, intent.OperationId);
            var document = new BsonDocument
            {
                ["_id"] = publication, ["ManifestKey"] = manifestKey, ["LibraryId"] = libraryId, ["DeviceId"] = target.DeviceId,
                ["PlanId"] = intent.PlanId, ["PlanRevision"] = intent.PlanRevision, ["ProfileId"] = sourceProfile.Id,
                ["ProfileRevision"] = sourceProfile.Revision, ["ManifestRevision"] = preview.ManifestRevision,
                ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = intent.OperationId,
                ["Destination"] = JsonSerializer.Serialize(target), ["NextStep"] = 0,
                ["StepCount"] = preview.Operations.Length, ["OwnedFileCount"] = preview.NextOwnedFiles.Length,
                ["LayoutEntryCount"] = preview.LayoutEntries.Length
            };
            if (previous is not null)
            {
                document["NextProfileId"] = preview.Plan.ProfileId;
                document["NextProfileRevision"] = preview.Plan.ProfileRevision;
            }
            database.GetCollection<BsonDocument>(ExportPublicationJournal).Insert(document);
            WriteJournalItems(publication, "Step", preview.Operations);
            WriteJournalItems(publication, "NextOwned", preview.NextOwnedFiles);
            WriteJournalItems(publication, "LayoutEntry", preview.LayoutEntries);
            database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
            {
                ["_id"] = OperationId(key), ["OriginInstanceId"] = key.OriginInstanceId, ["OperationId"] = key.OperationId,
                ["IntentFingerprint"] = PublicationFingerprint(libraryId, intent),
                ["Receipt"] = new BsonDocument { ["Type"] = PendingPublicationReceipt, ["Version"] = 1, ["PublicationId"] = publication }
            });
            var work = new PendingDeviceExportPublication(publication, key, intent.PlanId, intent.PlanRevision, target, preview.ManifestRevision, 0,
                preview.Operations);
            return new HostOutcome<DeviceExportPublicationReservation>.Success(new DeviceExportPublicationReservation.Pending(work));
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<int>> RecordPublicationStepAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid publicationId, int ordinal,
        CancellationToken token) => ExecuteAsync<int>(_ =>
        {
            if (!IsPublicationCaller(libraryId, caller))
                return new HostOutcome<int>.Forbidden();
            var journal = database.GetCollection<BsonDocument>(ExportPublicationJournal);
            var row = journal.FindById(publicationId);
            if (row is null || row["LibraryId"].AsGuid != libraryId || row["OriginInstanceId"].AsGuid != caller.InstanceId)
                return new HostOutcome<int>.Rejected("export-publication-unavailable");
            var next = row["NextStep"].AsInt32;
            if (ordinal < 0 || ordinal > next || ordinal >= row["StepCount"].AsInt32)
                return new HostOutcome<int>.Rejected("export-publication-step-mismatch", next, Conflict: true);
            if (ordinal == next)
            {
                row["NextStep"] = ++next;
                journal.Update(row);
            }
            return new HostOutcome<int>.Success(next);
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<CompletedDeviceExportPublication>> CompletePublicationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, Guid publicationId, CancellationToken token) => ExecuteAsync<CompletedDeviceExportPublication>(_ =>
        {
            var found = ReadPublicationReservation(libraryId, caller, intent);
            if (found is not HostOutcome<DeviceExportPublicationReservation?>.Success { Value: var reservation })
                return found.PropagateFailure<CompletedDeviceExportPublication>();
            if (reservation is DeviceExportPublicationReservation.Completed matching && matching.Receipt.PublicationId == publicationId)
                return new HostOutcome<CompletedDeviceExportPublication>.Success(matching.Receipt);
            if (reservation is not DeviceExportPublicationReservation.Pending { Work: var work } || work.PublicationId != publicationId)
                return new HostOutcome<CompletedDeviceExportPublication>.Rejected("export-publication-unavailable");
            if (work.NextStep != work.Operations.Length)
                return new HostOutcome<CompletedDeviceExportPublication>.Rejected("export-publication-incomplete", Conflict: true);

            var journal = database.GetCollection<BsonDocument>(ExportPublicationJournal);
            var row = journal.FindById(publicationId);
            var current = CheckPublicationInputs<CompletedDeviceExportPublication>(work.Destination, work.PlanId, work.PlanRevision,
                work.ManifestRevision, row["ProfileId"].AsString, row["ProfileRevision"].AsInt64);
            if (current is not null)
                return current;
            var nextProfile = Text(row, "NextProfileId");
            if (nextProfile is not null && CheckProfileSlot<CompletedDeviceExportPublication>(work.Destination, work.PlanId, nextProfile) is { } conflict)
                return conflict;
            var activeProfile = nextProfile ?? row["ProfileId"].AsString;
            var activeProfileRevision = nextProfile is null ? row["ProfileRevision"].AsInt64 : row["NextProfileRevision"].AsInt64;
            var scope = row["ManifestKey"].AsString;
            var nextOwned = ReadJournalItems<OwnedDeviceExportFile>(publicationId, "NextOwned", row["OwnedFileCount"].AsInt32);
            var revision = checked(work.ManifestRevision + 1);
            WriteOwnedFiles(work.Destination, scope, nextOwned);
            database.GetCollection<BsonDocument>(ExportManifestCollection).Upsert(new BsonDocument
            {
                ["_id"] = scope, ["LibraryId"] = libraryId, ["DeviceId"] = work.Destination.DeviceId,
                ["ShareId"] = work.Destination.ShareId, ["Revision"] = revision, ["PublicationId"] = publicationId,
                ["ProfileId"] = activeProfile, ["ProfileRevision"] = activeProfileRevision
            });

            var plans = database.GetCollection<BsonDocument>(DeviceExportCollection);
            var plan = plans.FindById(work.PlanId);
            if (nextProfile is not null || row["LayoutEntryCount"].IsInt32 && row["LayoutEntryCount"].AsInt32 > 0)
            {
                var layout = ReadJournalItems<ReviewedDeviceExportEntry>(publicationId, "LayoutEntry", row["LayoutEntryCount"].AsInt32);
                WriteEntries(work.PlanId, work.Destination.CaseSensitivePaths, layout);
            }
            if (nextProfile is not null)
            {
                plan["ScopeKey"] = ScopeKey(libraryId, work.Destination.DeviceId, activeProfile);
                plan["ProfileId"] = activeProfile;
                plan["ProfileVersion"] = activeProfileRevision;
            }
            plan["PublicationState"] = "Published";
            plans.Update(plan);

            var receiptTable = database.GetCollection<BsonDocument>("operation_receipts");
            var receiptRow = receiptTable.FindById(OperationId(work.Operation));
            var transferId = work.Operations.IsEmpty ? (Guid?)null : QueueDeviceExportScan(work, scope, revision);
            var result = new CompletedDeviceExportPublication(publicationId, work.PlanId, work.PlanRevision, revision, transferId);
            receiptRow["Receipt"] = new BsonDocument { ["Type"] = CompletedPublicationReceipt, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) };
            receiptTable.Update(receiptRow);
            database.GetCollection<BsonDocument>(ExportPublicationItems).DeleteMany(Query.EQ("PublicationId", publicationId));
            journal.Delete(publicationId);
            return new HostOutcome<CompletedDeviceExportPublication>.Success(result);
        }, token);

    private HostOutcome<DeviceExportPublicationReservation?> ReadPublicationReservation(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent)
    {
        if (!IsPublicationCaller(libraryId, caller))
            return new HostOutcome<DeviceExportPublicationReservation?>.Forbidden();
        if (intent.OperationId == Guid.Empty || intent.PlanId == Guid.Empty || intent.PlanRevision <= 0)
            return new HostOutcome<DeviceExportPublicationReservation?>.Rejected("invalid-export-publication-intent");

        var key = new HostOperationKey(caller.InstanceId, intent.OperationId);
        var saved = database.GetCollection<BsonDocument>("operation_receipts").FindById(OperationId(key));
        if (saved is null)
            return new HostOutcome<DeviceExportPublicationReservation?>.Success(null);
        var receipt = saved["Receipt"].AsDocument;
        if (receipt["Version"].AsInt32 != 1 || saved["IntentFingerprint"].AsString != PublicationFingerprint(libraryId, intent))
            return new HostOutcome<DeviceExportPublicationReservation?>.Rejected("operation-intent-mismatch", Conflict: true);

        switch (receipt["Type"].AsString)
        {
            case CompletedPublicationReceipt:
                return new HostOutcome<DeviceExportPublicationReservation?>.Success(
                    new DeviceExportPublicationReservation.Completed(ReadStored<CompletedDeviceExportPublication>(receipt["Result"])));
            case PendingPublicationReceipt:
                var id = receipt["PublicationId"].AsGuid;
                var others = database.GetCollection<BsonDocument>(ExportPublicationJournal).FindById(id);
                if (others is null || others["OriginInstanceId"].AsGuid != caller.InstanceId || others["OperationId"].AsGuid != intent.OperationId ||
                    others["LibraryId"].AsGuid != libraryId || others["PlanId"].AsGuid != intent.PlanId || others["PlanRevision"].AsInt64 != intent.PlanRevision)
                    throw new IOException("The stored export operation has no matching publication journal.");
                var operations = ReadJournalItems<DeviceExportFileOperation>(id, "Step", others["StepCount"].AsInt32);
                return new HostOutcome<DeviceExportPublicationReservation?>.Success(new DeviceExportPublicationReservation.Pending(new(id, key,
                    intent.PlanId, intent.PlanRevision, ReadStoredExportTarget(others["Destination"]),
                    others["ManifestRevision"].AsInt64, others["NextStep"].AsInt32, operations)));
            default:
                return new HostOutcome<DeviceExportPublicationReservation?>.Rejected("operation-intent-mismatch", Conflict: true);
        }
    }

    private HostOutcome<T>? CheckPublicationInputs<T>(RegisteredAndroidExport target, Guid planId, long planRevision, long manifestRevision,
        string profileId, long profileRevision)
    {
        var registered = ReadRegisteredAndroidExport(target.LibraryId, target.DeviceId);
        if (registered is null || registered != target)
            return new HostOutcome<T>.Rejected("export-registration-changed", registered?.Revision, Conflict: true);
        var plan = database.GetCollection<BsonDocument>(DeviceExportCollection).FindById(planId);
        if (plan is null || plan["LibraryId"].AsGuid != target.LibraryId || plan["DeviceId"].AsGuid != target.DeviceId)
            return new HostOutcome<T>.Rejected("export-plan-unavailable");
        if (plan["ManifestRevision"].AsInt64 != planRevision || plan["ShareId"].AsString != target.ShareId ||
            plan["ProfileId"].AsString != profileId || plan["ProfileVersion"].AsInt64 != profileRevision ||
            plan["CaseSensitivePaths"].AsBoolean != target.CaseSensitivePaths)
            return new HostOutcome<T>.Rejected("export-plan-changed", plan["ManifestRevision"].AsInt64, Conflict: true);
        var actual = ManifestRevision(ManifestKey(target));
        return actual != manifestRevision ? new HostOutcome<T>.Rejected("export-manifest-changed", actual, Conflict: true) : null;
    }

    private bool IsPublicationCaller(Guid libraryId, AuthenticatedHostInstance caller) => libraryId != Guid.Empty && caller.LibraryId == libraryId &&
        caller.InstanceId != Guid.Empty && IsActiveInstance(database.GetCollection<BsonDocument>("registered_instances"), libraryId, caller.InstanceId);

    private bool HasPendingPublication(RegisteredAndroidExport target) => database.GetCollection<BsonDocument>(ExportPublicationJournal)
        .FindOne(Query.EQ("ManifestKey", ManifestKey(target))) is not null;

    private long ManifestRevision(string key) => database.GetCollection<BsonDocument>(ExportManifestCollection).FindById(key)?["Revision"].AsInt64 ?? 0;

    private static string ManifestKey(RegisteredAndroidExport target) => $"{target.LibraryId:N}/{target.DeviceId:N}/{HashKey(target.ShareId)}";

    private static string PublicationFingerprint(Guid libraryId, DeviceExportPublicationIntent intent) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Operation = "publish-device-export", Version = 1, LibraryId = libraryId, Intent = intent })));

    private void WriteJournalItems<T>(Guid publication, string kind, ImmutableArray<T> items)
    {
        var table = database.GetCollection<BsonDocument>(ExportPublicationItems);
        for (var index = 0; index < items.Length; index++)
            table.Insert(new BsonDocument
            {
                ["_id"] = $"{publication:N}/{kind}/{index.ToString(CultureInfo.InvariantCulture)}", ["PublicationId"] = publication,
                ["Ordinal"] = index, ["Kind"] = kind, ["Payload"] = JsonSerializer.Serialize(items[index])
            });
    }

    private ImmutableArray<T> ReadJournalItems<T>(Guid id, string kind, int count) where T : class
    {
        var rows = database.GetCollection<BsonDocument>(ExportPublicationItems).Find(Query.EQ("PublicationId", id))
            .Where(row => row["Kind"].AsString == kind).OrderBy(row => row["Ordinal"].AsInt32).ToArray();
        if (rows.Length != count || rows.Where((row, index) => row["Ordinal"].AsInt32 != index).Any())
            throw new IOException("The publication journal contains an incomplete or disordered file list.");
        return [.. rows.Select(row => ReadStored<T>(row["Payload"]))];
    }

    private static T ReadStored<T>(BsonValue payload) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload.AsString) ?? throw new IOException("A stored publication record is empty."); }
        catch (JsonException exception) { throw new IOException("A stored publication record is unreadable.", exception); }
    }

    private static OwnedDeviceExportFile ReadOwnedFile(BsonDocument row) => new(new((ExportContentKind)row["SourceKind"].AsInt32,
        row["SourceEntityId"].AsGuid), row["RelativePath"].AsString, row["ContentIdentity"].AsString);

    private void WriteOwnedFiles(RegisteredAndroidExport target, string key, ImmutableArray<OwnedDeviceExportFile> files)
    {
        var table = database.GetCollection<BsonDocument>(ExportOwnedFileCollection);
        table.DeleteMany(Query.EQ("ManifestKey", key));
        foreach (var file in files)
        {
            var pathKey = target.CaseSensitivePaths ? file.Path : file.Path.ToUpperInvariant();
            table.Insert(new BsonDocument
            {
                ["_id"] = $"{key}/{HashKey(pathKey)}", ["ManifestKey"] = key, ["PathKey"] = pathKey, ["RelativePath"] = file.Path,
                ["SourceKind"] = (int)file.Source.Kind, ["SourceEntityId"] = file.Source.EntityId, ["ContentIdentity"] = file.ContentIdentity
            });
        }
    }

    private static void EnsurePublicationIndexes(ILiteDatabase database)
    {
        database.GetCollection<BsonDocument>(ExportPublicationJournal).EnsureIndex("ManifestKey", unique: true);
        database.GetCollection<BsonDocument>(ExportPublicationItems).EnsureIndex("PublicationId");
        database.GetCollection<BsonDocument>(ExportOwnedFileCollection).EnsureIndex("ManifestKey");
    }
}
