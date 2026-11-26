using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Journals the complete approved batch and atomically publishes finished playlist-only batches.</summary>
/// <remarks>2026-11-24: Fence the reviewed intent and child operations for FR-LIBRARY-002#ac-batch-review, ac-batch-commit and FR-RECORDS-001#ac-replicate.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IApprovedLibraryBatchStore
{
    private const string ApprovedBatchPublications = "library_batch_publications";
    private const string ApprovedBatchEntries = "library_batch_publication_entries";
    private const string BatchApprovalReceipt = "ApprovedLibraryBatch";

    /// <inheritdoc />
    Task<HostOutcome<ApprovedLibraryBatchStatus>> IApprovedLibraryBatchStore.SubmitBatchAsync(Guid library, AuthenticatedHostInstance caller,
        ApproveLibraryBatchRequest request, CancellationToken token) => ExecuteAsync<ApprovedLibraryBatchStatus>(_ =>
    {
        if (!IsImportCaller(library, caller) || caller.InstanceId != request.OriginInstanceId)
            return new HostOutcome<ApprovedLibraryBatchStatus>.Forbidden();
        var operation = OperationId(new(caller.InstanceId, request.OperationId));
        var batchKey = BatchKey(library, caller.InstanceId, request.BatchId);
        var fingerprint = HashKey($"approved-library-batch/v1/{library:N}/" + JsonSerializer.Serialize(request));
        var receipts = database.GetCollection<BsonDocument>("operation_receipts");
        var journals = database.GetCollection<BsonDocument>(ApprovedBatchPublications);
        if (receipts.FindById(operation) is { } previous)
        {
            var type = Text(previous["Receipt"].AsDocument, "Type");
            if (type == BatchWithdrawalReceipt && journals.FindById(operation) is { } withdrawn &&
                MatchesBatchReview(withdrawn, library, caller.InstanceId, request.BatchId, request.BatchRevision, request.ReviewId))
                return BatchConflict("library-batch-withdrawn");
            if (Text(previous, "IntentFingerprint") != fingerprint || type != BatchApprovalReceipt)
                return BatchConflict("operation-intent-mismatch");
            var prior = ReadApprovedBatch(library, caller.InstanceId, request.OperationId, journals);
            return prior is HostOutcome<ApprovedLibraryBatchStatus>.Success { Value.Phase: BatchPublicationPhase.Withdrawn }
                ? BatchConflict("library-batch-withdrawn") : prior;
        }
        if (journals.FindOne(Query.EQ("BatchKey", batchKey)) is not null)
            return BatchConflict("library-batch-already-admitted");
        if (request.Entries.Any(entry => receipts.Exists(Query.EQ("_id", OperationId(new(caller.InstanceId, entry.OperationId))))))
            return BatchConflict("library-batch-entry-already-submitted");

        if (request.Entries.Any(entry => entry.Change is ApprovedBatchChange.Playlist { PlaylistId: { } id } &&
                IsPlaylistReserved(database, library, id)))
            return BatchConflict("playlist-reserved-by-batch");
        var playlistWork = PrepareBatchPlaylists(library, request.Entries);
        if (playlistWork is not HostOutcome<PreparedBatchPlaylists>.Success { Value: var playlists })
            return playlistWork.PropagateFailure<ApprovedLibraryBatchStatus>();
        var tagTargets = new Dictionary<Guid, MaintenanceTrack>();
        var checkedEffects = ReserveNativeBatchEntries(library, caller, request, tagTargets, token);
        if (checkedEffects is not HostOutcome<bool>.Success)
            return checkedEffects.PropagateFailure<ApprovedLibraryBatchStatus>();

        long? publishedRevision = request.Entries.All(entry => entry.Change is ApprovedBatchChange.Playlist)
            ? PublishBatchPlaylists(library, new(caller.InstanceId, request.OperationId), playlists) : null;
        var importCount = request.Entries.Select(entry => entry.Change).OfType<ApprovedBatchChange.Import>().Sum(entry => entry.Files.Length);
        var result = new ApprovedLibraryBatchStatus(library, caller.InstanceId, request.OperationId, request.BatchId,
            request.BatchRevision, request.ReviewId, publishedRevision.HasValue ? BatchPublicationPhase.Published : BatchPublicationPhase.Accepted,
            publishedRevision, publishedRevision.HasValue ? playlists.Results : [], importCount, []);
        journals.Insert(new BsonDocument { ["_id"] = operation, ["BatchKey"] = batchKey, ["LibraryId"] = library, ["OriginInstanceId"] = caller.InstanceId,
            ["OperationId"] = request.OperationId, ["BatchId"] = request.BatchId, ["BatchRevision"] = request.BatchRevision, ["ReviewId"] = request.ReviewId,
            ["Phase"] = result.Phase.ToString(), ["EntryCount"] = request.Entries.Length, ["ImportPartCount"] = importCount,
            ["Status"] = JsonSerializer.Serialize(result) });
        SaveBatchReservation(receipts, caller.InstanceId, request.OperationId, request.OperationId, fingerprint, BatchApprovalReceipt);
        var rows = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
        foreach (var entry in request.Entries)
        {
            token.ThrowIfCancellationRequested();
            var row = new BsonDocument { ["_id"] = string.Create(CultureInfo.InvariantCulture, $"{operation}/{entry.Position}"), ["PublicationKey"] = operation,
                ["Position"] = entry.Position, ["EntryOperationId"] = entry.OperationId, ["Kind"] = entry.Change.GetType().Name,
                ["Payload"] = JsonSerializer.Serialize(entry.Change) };
            if (tagTargets.TryGetValue(entry.OperationId, out var target))
                row["TagTarget"] = JsonSerializer.Serialize(target);
            rows.Insert(row);
            SaveBatchReservation(receipts, caller.InstanceId, entry.OperationId, request.OperationId, fingerprint, "LibraryBatchEntryReserved");
        }
        return new HostOutcome<ApprovedLibraryBatchStatus>.Success(result);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<ApprovedLibraryBatchStatus>> IApprovedLibraryBatchStore.ReadBatchAsync(Guid library, AuthenticatedHostInstance caller,
        Guid operation, CancellationToken token) => OnLaneAsync<ApprovedLibraryBatchStatus>(() => IsImportCaller(library, caller)
        ? ReadApprovedBatch(library, caller.InstanceId, operation, database.GetCollection<BsonDocument>(ApprovedBatchPublications))
        : new HostOutcome<ApprovedLibraryBatchStatus>.Forbidden(), token);

    /// <summary>Returns the approval with the current private transfer receipts for FR-LIBRARY-002#ac-batch-commit.</summary>
    private HostOutcome<ApprovedLibraryBatchStatus> ReadApprovedBatch(Guid library, Guid origin, Guid operation, ILiteCollection<BsonDocument> table)
    {
        if (table.FindById(OperationId(new(origin, operation))) is not { } row || row["LibraryId"].AsGuid != library)
            return new HostOutcome<ApprovedLibraryBatchStatus>.Rejected("library-batch-unavailable");
        return new HostOutcome<ApprovedLibraryBatchStatus>.Success(ProjectBatchImportStatus(row));
    }

    private static HostOutcome<ApprovedLibraryBatchStatus> BatchConflict(string code) => new HostOutcome<ApprovedLibraryBatchStatus>.Rejected(code, Conflict: true);

    private static void SaveBatchReservation(ILiteCollection<BsonDocument> receipts, Guid origin, Guid operation, Guid approval, string fingerprint, string type) =>
        receipts.Insert(new BsonDocument { ["_id"] = OperationId(new(origin, operation)), ["OriginInstanceId"] = origin, ["OperationId"] = operation,
            ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument { ["Type"] = type, ["Version"] = 1, ["ApprovalOperationId"] = approval } });

    private HostOutcome<bool> ReserveNativeBatchEntries(Guid library, AuthenticatedHostInstance caller, ApproveLibraryBatchRequest request,
        Dictionary<Guid, MaintenanceTrack> targets, CancellationToken token)
    {
        var plans = database.GetCollection<BsonDocument>(MaintenancePlans);
        var tagsByTrack = new Dictionary<Guid, ImmutableDictionary<string, string>>();
        foreach (var entry in request.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Change is ApprovedBatchChange.Maintenance maintenance)
            {
                var proposed = maintenance.Plan;
                var row = plans.FindById(proposed.PlanId);
                if (row is null || row["LibraryId"].AsGuid != library || row["OriginInstanceId"].AsGuid != caller.InstanceId ||
                    row["JobId"].IsGuid || row["BatchOperationId"].IsGuid)
                    return new HostOutcome<bool>.Rejected("library-batch-maintenance-unavailable", Conflict: true);
                var recorded = ReadStored<LibraryMaintenancePlan>(row["Plan"]);
                if (proposed.Kind != recorded.Kind || proposed.CatalogRevision != recorded.CatalogRevision ||
                    !proposed.TrackIds.SequenceEqual(recorded.TrackIds) || !proposed.Moves.SequenceEqual(recorded.Moves) ||
                    !proposed.AudioPaths.SequenceEqual(recorded.AudioPaths) || (recorded.Kind == LibraryMaintenanceKind.Organize && recorded.Moves.IsEmpty))
                    return new HostOutcome<bool>.Rejected("library-batch-maintenance-changed", Conflict: true);
                var maintenanceTargets = ReadStored<MaintenanceTrack[]>(row["Targets"]).ToImmutableArray();
                if (recorded.Kind == LibraryMaintenanceKind.RemoveAudio && !AudioRemoval.ManifestMatches(maintenanceTargets, ReadAudioRemovalFiles(row)))
                    return new HostOutcome<bool>.Rejected("audio-removal-preview-required", Conflict: true);
                var check = RecheckMaintenance(recorded, caller, maintenanceTargets);
                if (check is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success)
                    return check.PropagateFailure<bool>();
                row["BatchOperationId"] = request.OperationId;
                plans.Update(row);
            }
            else if (entry.Change is ApprovedBatchChange.TrackTags tags)
            {
                var track = ReadTagTrack(library, tags.TrackId);
                if (track is null || track["Revision"].AsInt64 != tags.ExpectedRevision)
                    return new HostOutcome<bool>.Rejected("stale-track-tags", track?["Revision"].AsInt64, Conflict: true);
                var selected = ReadMaintenanceTracks(library, [tags.TrackId]);
                if (selected is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var tracks })
                    return selected.PropagateFailure<bool>();
                targets.Add(entry.OperationId, tracks.Single());
                var baseline = tagsByTrack.GetValueOrDefault(tags.TrackId) ?? ProjectTrackTags(library, track);
                if (baseline.Count != tags.Before.Count || baseline.Any(value => tags.Before.GetValueOrDefault(value.Key) != value.Value))
                    return new HostOutcome<bool>.Rejected("library-batch-tag-baseline-changed", tags.ExpectedRevision, Conflict: true);
                foreach (var (field, value) in tags.Changes)
                    baseline = string.IsNullOrEmpty(value) ? baseline.Remove(field) : baseline.SetItem(field, value);
                tagsByTrack[tags.TrackId] = baseline;
            }
        }
        return new HostOutcome<bool>.Success(true);
    }

    private HostOutcome<PreparedBatchPlaylists> PrepareBatchPlaylists(Guid library, ImmutableArray<ApprovedBatchEntry> entries)
    {
        var saved = database.GetCollection<BsonDocument>("saved_playlists");
        var newIds = new Dictionary<Guid, Guid>();
        var final = new Dictionary<Guid, SavedHostPlaylist>();
        var submitted = new List<(Guid Operation, Guid Playlist)>();
        foreach (var entry in entries)
        {
            if (entry.Change is not ApprovedBatchChange.Playlist playlist)
                continue;
            SavedHostPlaylist? baseline = null;
            Guid playlistId;
            if (playlist.PlaylistId is { } existingId)
            {
                baseline = MatchPlaylist(saved, library, existingId);
                if (baseline is null || baseline.Revision != playlist.ExpectedRevision)
                    return new HostOutcome<PreparedBatchPlaylists>.Rejected("stale-saved-playlist", baseline?.Revision, Conflict: true);
                playlistId = existingId;
            }
            else if (!newIds.TryGetValue(playlist.Source.SourceId, out playlistId))
            {
                playlistId = Guid.NewGuid();
                newIds.Add(playlist.Source.SourceId, playlistId);
            }
            var prior = final.GetValueOrDefault(playlistId) ?? baseline;
            final[playlistId] = new(library, playlistId, checked((prior?.Revision ?? 0) + 1), playlist.Name, playlist.Entries);
            submitted.Add((entry.OperationId, playlistId));
        }
        return new HostOutcome<PreparedBatchPlaylists>.Success(new([.. final.Values], [.. submitted.Select(item =>
            new BatchPublishedPlaylist(item.Operation, item.Playlist, final[item.Playlist].Revision))]));
    }

    private long PublishBatchPlaylists(Guid library, HostOperationKey operation, PreparedBatchPlaylists prepared)
    {
        var head = ReadFeedHead(database, library);
        var revision = checked(head["CurrentRevision"].AsInt64 + 1);
        var updates = new BsonArray();
        WriteBatchPlaylists(prepared, updates);
        AppendFeed(database, head, revision, operation, updates);
        return revision;
    }

    /// <summary>Adds the playlist changes to the encompassing publication transaction for FR-LIBRARY-002#ac-batch-commit.</summary>
    private void WriteBatchPlaylists(PreparedBatchPlaylists prepared, BsonArray updates)
    {
        var playlists = database.GetCollection<BsonDocument>("saved_playlists");
        foreach (var final in prepared.Playlists)
        {
            var row = WritePlaylist(final);
            playlists.Upsert(row);
            updates.Add(FeedUpsert("Playlist", row));
        }
    }

    private sealed record PreparedBatchPlaylists(ImmutableArray<SavedHostPlaylist> Playlists, ImmutableArray<BatchPublishedPlaylist> Results);
}
