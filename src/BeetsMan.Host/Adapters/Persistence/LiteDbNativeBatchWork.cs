using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Owns the execution cursor, playlist reservation and publication transaction for approved native batches.</summary>
/// <remarks>2026-11-24: Recover the reviewed order and publish one peer revision for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Store prepared and read-back albums within the same approval for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed partial class LiteDbHostPlaylistStore : INativeBatchWorkStore
{
    /// <inheritdoc />
    Task<HostOutcome<NativeBatchWork?>> INativeBatchWorkStore.ReadNextNativeBatchAsync(Guid library, CancellationToken token) => OnLaneAsync<NativeBatchWork?>(() =>
    {
        var pending = BatchJournals(database).Find(Query.And(Query.EQ("LibraryId", library),
            Query.In("Phase", new BsonArray(nameof(BatchPublicationPhase.Accepted), nameof(BatchPublicationPhase.Running)))))
            .OrderByDescending(row => Text(row, "Phase") == nameof(BatchPublicationPhase.Running)).ThenBy(row => row["_id"].AsString, StringComparer.Ordinal);
        foreach (var row in pending)
        {
            token.ThrowIfCancellationRequested();
            var status = ProjectBatchImportStatus(row);
            var rows = database.GetCollection<BsonDocument>(ApprovedBatchEntries).Find(Query.EQ("PublicationKey", row["_id"]))
                .OrderBy(entry => entry["Position"].AsInt32).ToArray();
            if (rows.Length != row["EntryCount"].AsInt32 || !rows.Select(entry => entry["Position"].AsInt32).SequenceEqual(Enumerable.Range(1, rows.Length)))
                return new HostOutcome<NativeBatchWork?>.Unavailable("library-batch-journal-unavailable");
            var entries = rows.Select(entry => new ApprovedBatchEntry(entry["EntryOperationId"].AsGuid, entry["Position"].AsInt32,
                ReadStored<ApprovedBatchChange>(entry["Payload"]))).ToArray();
            if (entries.Any(entry => entry.Change is not (ApprovedBatchChange.TrackTags or ApprovedBatchChange.Playlist or ApprovedBatchChange.Import or
                    ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveCatalog or LibraryMaintenanceKind.Organize, AudioDeletionApproved: false } or
                    ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveAudio, AudioDeletionApproved: true })))
            {
                if (status.Phase == BatchPublicationPhase.Running)
                    return new HostOutcome<NativeBatchWork?>.Unavailable("library-batch-entry-executor-unavailable");
                continue;
            }

            var journal = entries.Select((entry, index) => ReadNativeBatchEntry(status, row, rows[index], entry)).ToImmutableArray();
            if (status.Phase == BatchPublicationPhase.Accepted && journal.Any(entry => entry.Approved.Change is ApprovedBatchChange.Import && entry.Import is null))
                continue;
            var position = row["NextPosition"].IsInt32 ? row["NextPosition"].AsInt32 : 1;
            var work = new NativeBatchWork(status, journal, position, EntryAttempted(row));
            if (position < 1 || position > journal.Length + 1 || status.Phase == BatchPublicationPhase.Running && !CheckBatchCheckpoints(work))
                return new HostOutcome<NativeBatchWork?>.Unavailable("library-batch-journal-unavailable");
            return new HostOutcome<NativeBatchWork?>.Success(work);
        }
        return new HostOutcome<NativeBatchWork?>.Success(null);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.BeginNativeBatchAsync(NativeBatchWork work, string? rejection, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var row = CurrentBatch(work, BatchPublicationPhase.Accepted);
        if (row is null)
            return new HostOutcome<bool>.Success(false);
        var status = work.Status;
        var library = status.LibraryId;
        var existingPlaylists = BatchPlaylists(work).Select(entry => entry.PlaylistId).OfType<Guid>().Distinct().ToArray();
        if (rejection is null)
        {
            if (!IsImportCaller(library, new(library, status.OriginInstanceId)))
                rejection = "library-batch-origin-unavailable";
            else if (!TargetsCurrent(work) || !TagProjectionAvailable(work))
                rejection = "library-batch-track-baseline-changed";
            else if (!DestinationsUnclaimed(library, work.Entries.SelectMany(entry => entry.Moves).Select(move => move.Destination)))
                rejection = "library-batch-organization-path-conflict";
            else if (!AudioPathsAreExclusive(library, [.. work.Entries.Where(entry => entry.DeletesAudio).SelectMany(entry => entry.Targets)]))
                rejection = "audio-removal-path-still-referenced";
            else if (existingPlaylists.Any(id => IsPlaylistReserved(database, library, id)) ||
                PrepareBatchPlaylists(library, ApprovedEntries(work)) is not HostOutcome<PreparedBatchPlaylists>.Success)
                rejection = "library-batch-playlist-baseline-changed";
        }
        if (rejection is not null)
        {
            SaveBatchPhase(row, status with { Phase = BatchPublicationPhase.Rejected, FailureCode = rejection });
            return new HostOutcome<bool>.Success(true);
        }
        // Older accepted approvals did not store a separate copy of the pinned tag target.
        var entries = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
        foreach (var entry in work.Entries.Where(entry => entry.Approved.Change is ApprovedBatchChange.TrackTags))
        {
            var id = FormattableString.Invariant($"{row["_id"].AsString}/{entry.Approved.Position}");
            var journal = entries.FindById(id);
            if (journal["TagTarget"].IsString)
                continue;
            journal["TagTarget"] = JsonSerializer.Serialize(entry.Targets.Single());
            entries.Update(journal);
        }
        row["NextPosition"] = 1;
        row["EntryAttempted"] = false;
        row["LockedPlaylists"] = new BsonArray(existingPlaylists.Select(id => new BsonValue(id)));
        SaveBatchPhase(row, status with { Phase = BatchPublicationPhase.Running });
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.SaveNativeBatchImportPreparationAsync(NativeBatchWork work, NativeBatchImport import,
        PreparedImportAlbum manifest, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        if (CurrentBatch(work, BatchPublicationPhase.Accepted) is not { } row)
            return new HostOutcome<bool>.Success(false);
        var entries = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
        if (entries.FindById(BatchImportEntryKey(row["_id"].AsString, import.Decision.Position)) is not { } journal ||
            !journal["ImportChoice"].IsString || ReadStored<ApprovedBatchImportChoiceReceipt>(journal["ImportChoice"]) != import.Decision ||
            journal["PreparedImport"].IsString)
            return new HostOutcome<bool>.Success(false);
        journal["PreparedImport"] = JsonSerializer.Serialize(manifest);
        return new HostOutcome<bool>.Success(entries.Update(journal));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.MarkNativeBatchEntryAttemptedAsync(NativeBatchWork work, CancellationToken token) =>
        WriteBatchCursorAsync(work, work.NextPosition, attempted: true, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.MarkBatchOrganizationAttemptedAsync(NativeBatchWork work, ImmutableArray<OrganizationCopy> copies,
        CancellationToken token)
    {
        var entry = work.Entries[work.NextPosition - 1];
        if (work.EntryAttempted || entry.Approved.Change is not ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.Organize } change ||
            !OrganizationManifestMatches(change.Plan, entry.Targets, copies))
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Unavailable("library-batch-organization-checkpoint-invalid"));
        return WriteBatchCursorAsync(work, work.NextPosition, attempted: true, token, journal => journal["OrganizationCopies"] = JsonSerializer.Serialize(copies));
    }

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.AdvanceNativeBatchEntryAsync(NativeBatchWork work, CancellationToken token, OrganizationFileStamp? taggedFile)
    {
        if (taggedFile is null)
            return WriteBatchCursorAsync(work, checked(work.NextPosition + 1), attempted: false, token);
        if (!taggedFile.IsValid())
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Unavailable("library-batch-organization-checkpoint-invalid"));
        return WriteBatchCursorAsync(work, checked(work.NextPosition + 1), attempted: false, token,
            journal => journal["TaggedFile"] = JsonSerializer.Serialize(taggedFile));
    }

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.RecordNativeBatchImportAsync(NativeBatchWork work, ReconciledImportAlbum album, CancellationToken token) =>
        WriteBatchCursorAsync(work, checked(work.NextPosition + 1), attempted: false, token,
            journal => journal["ObservedImport"] = JsonSerializer.Serialize(album));

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.ResetNativeBatchImportAttemptAsync(NativeBatchWork work, CancellationToken token) =>
        WriteBatchCursorAsync(work, work.NextPosition, attempted: false, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> INativeBatchWorkStore.PublishNativeBatchAsync(NativeBatchWork work, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var row = CurrentBatch(work, BatchPublicationPhase.Running);
        if (row is null || work.NextPosition != work.Entries.Length + 1 || work.EntryAttempted)
            return new HostOutcome<bool>.Success(false);
        var status = work.Status;
        var library = status.LibraryId;
        var playlists = PrepareBatchPlaylists(library, ApprovedEntries(work));
        var paths = work.Entries.SelectMany(entry => entry.Moves).ToDictionary(move => move.TrackId, move => move.Destination);
        if (playlists is not HostOutcome<PreparedBatchPlaylists>.Success { Value: var prepared } || !TargetsCurrent(work) || !DestinationsUnclaimed(library, paths.Values))
            return new HostOutcome<bool>.Unavailable("library-batch-publication-baseline-changed");

        var head = ReadFeedHead(database, library);
        var revision = checked(head["CurrentRevision"].AsInt64 + 1);
        var updates = new BsonArray();
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        var removed = work.Entries.Where(entry => entry.RemovesCatalog)
            .SelectMany(entry => entry.Targets).Select(target => target.TrackId).ToHashSet();
        var patches = work.Entries.Select(entry => entry.Approved.Change).OfType<ApprovedBatchChange.TrackTags>()
            .GroupBy(edit => edit.TrackId).ToDictionary(group => group.Key, group => group.SelectMany(edit => edit.Changes)
                .GroupBy(change => change.Key, StringComparer.Ordinal).ToImmutableDictionary(fields => fields.Key, fields => fields.Last().Value, StringComparer.Ordinal));
        foreach (var entry in work.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Import?.Observed is { } album && !TryProjectImportAlbum(album, revision, updates))
                return new HostOutcome<bool>.Unavailable("library-batch-import-catalog-unavailable");
            if (entry.RemovesCatalog)
            {
                foreach (var target in entry.Targets)
                {
                    if (!tracks.Delete(target.TrackId))
                        return new HostOutcome<bool>.Unavailable("maintenance-catalog-delete-uncommitted");
                    updates.Add(TrackTombstone(library, target.TrackId, revision));
                }
                continue;
            }
            IEnumerable<Guid> edited = entry.Approved.Change is ApprovedBatchChange.TrackTags edit ? [edit.TrackId] : entry.Moves.Select(move => move.TrackId);
            foreach (var trackId in edited)
            {
                if (removed.Contains(trackId))
                    continue;
                var hadPatch = patches.Remove(trackId, out var patch);
                var hadPath = paths.Remove(trackId, out var path);
                if (!hadPatch && !hadPath)
                    continue;
                var track = ReadTagTrack(library, trackId)!;
                if (hadPatch && !ApplyTagProjection(library, track, patch!, revision, updates))
                    return new HostOutcome<bool>.Unavailable("tag-catalog-artist-ambiguous");
                if (hadPath)
                    track["RelativePath"] = path;
                WriteCatalogChange(tracks, track, "Track", revision, updates);
            }
        }
        WriteBatchPlaylists(prepared, updates);
        if (updates.Count > 0)
            AppendFeed(database, head, revision, new(status.OriginInstanceId, status.OperationId), updates);
        else
            revision = head["CurrentRevision"].AsInt64;
        row.Remove("LockedPlaylists");
        SaveBatchPhase(row, status with { Phase = BatchPublicationPhase.Published, CatalogRevision = revision, Playlists = prepared.Results });
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <summary>Reloads the original target and any manifest or fingerprint written within the execution checkpoint.</summary>
    private NativeBatchEntry ReadNativeBatchEntry(ApprovedLibraryBatchStatus status, BsonDocument approval, BsonDocument row, ApprovedBatchEntry entry)
    {
        var targets = ReadBatchTargets(status, row, entry.Change, out var audioFiles);
        return new(entry, targets, row["OrganizationCopies"].IsString ? ReadStored<OrganizationCopy[]>(row["OrganizationCopies"]).ToImmutableArray() : [],
            row["TaggedFile"].IsString ? ReadStored<OrganizationFileStamp>(row["TaggedFile"]) : null, audioFiles,
            entry.Change is ApprovedBatchChange.Import import ? ReadNativeBatchImport(status, approval, row, entry, import) : null);
    }

    /// <summary>Rehydrates the explicit decision and its private native approval for the exact batch entry.</summary>
    private NativeBatchImport? ReadNativeBatchImport(ApprovedLibraryBatchStatus status, BsonDocument approval, BsonDocument row,
        ApprovedBatchEntry entry, ApprovedBatchChange.Import import)
    {
        if (!row["ImportChoice"].IsString || !row["ImportReview"].IsString || !HasAllReviewCopies(approval, row, import))
            return null;
        var review = ReadStored<StoredImportReview>(row["ImportReview"]);
        var decision = ReadStored<ApprovedBatchImportChoiceReceipt>(row["ImportChoice"]);
        if (decision.LibraryId != status.LibraryId || decision.OriginInstanceId != status.OriginInstanceId || decision.ApprovalOperationId != status.OperationId ||
            decision.EntryOperationId != entry.OperationId || decision.Position != entry.Position || decision.Revision != row["ImportReviewRevision"].AsInt64 ||
            decision.ReviewItemId != review.Snapshot.ItemId || decision.OperationId == Guid.Empty)
            return null;
        string? provider = null;
        ApprovedBeetsMatch? match = null;
        if (decision.Choice == ImportChoiceKind.UseMatch)
        {
            if (decision.MatchId is not Guid id || !review.ProviderKeys.TryGetValue(id, out provider) ||
                review.NativeMatches?.TryGetValue(id, out match) != true)
                return null;
        }
        else if (decision.MatchId is not null || decision.Choice is not (ImportChoiceKind.KeepTags or ImportChoiceKind.Skip))
            return null;
        return new(BuildBatchUpload(status.LibraryId, status.OriginInstanceId, entry.OperationId, import), decision, provider, match,
            row["PreparedImport"].IsString ? ReadStored<PreparedImportAlbum>(row["PreparedImport"]) : null,
            row["ObservedImport"].IsString ? ReadStored<ReconciledImportAlbum>(row["ObservedImport"]) : null);
    }

    /// <summary>Checks that every completed move or post-move tag has valid durable evidence for its step.</summary>
    private static bool CheckBatchCheckpoints(NativeBatchWork work)
    {
        var organized = new HashSet<Guid>();
        var position = work.NextPosition;
        foreach (var entry in work.Entries)
        {
            if (entry.RequiresTargets && entry.Targets.IsEmpty)
                return false;
            if (entry.DeletesAudio && !AudioRemoval.ManifestMatches(entry.Targets, entry.AudioFiles))
                return false;
            var index = entry.Approved.Position;
            if (entry.Approved.Change is ApprovedBatchChange.Import && !ValidImportCheckpoint(entry.Import, index < position))
                return false;
            if (entry.Approved.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.Organize } change &&
                (index < position || index == position && work.EntryAttempted))
            {
                if (!OrganizationManifestMatches(change.Plan, entry.Targets, entry.OrganizationCopies) || entry.Moves.Any(move => !organized.Add(move.TrackId)))
                    return false;
            }
            var needsStamp = entry.Approved.Change is ApprovedBatchChange.TrackTags edit && index < position &&
                work.NeedsTagFingerprint(edit.TrackId, index, organized.Contains(edit.TrackId));
            if (needsStamp ? entry.TaggedFile?.IsValid() != true : entry.TaggedFile is not null)
                return false;
        }
        return true;
    }

    /// <summary>Requires durable proof precisely for completed, non-skipped import entries.</summary>
    private static bool ValidImportCheckpoint(NativeBatchImport? import, bool completed)
    {
        if (import is null || (import.Decision.Choice == ImportChoiceKind.UseMatch
                ? import.Prepared?.Tracks.Length != import.Upload.Files.Length : import.Prepared is not null))
            return false;
        return completed && import.Decision.Choice != ImportChoiceKind.Skip
            ? import.Observed is { } album && import.Verify(album) is not null
            : import.Observed is null;
    }

    /// <summary>Protects existing playlists from competing edits while the containing batch is executing.</summary>
    private static bool IsPlaylistReserved(ILiteDatabase db, Guid library, Guid playlist) => BatchJournals(db)
        .Find(Query.And(Query.EQ("LibraryId", library), Query.EQ("Phase", nameof(BatchPublicationPhase.Running))))
        .Any(row => row["LockedPlaylists"].IsArray && row["LockedPlaylists"].AsArray.Contains(new BsonValue(playlist)));

    /// <summary>Loads the immutable targets recorded with the approval and its maintenance plan.</summary>
    private ImmutableArray<MaintenanceTrack> ReadBatchTargets(ApprovedLibraryBatchStatus status, BsonDocument row, ApprovedBatchChange change,
        out ImmutableArray<AudioRemovalFile> audioFiles)
    {
        audioFiles = [];
        if (change is ApprovedBatchChange.TrackTags edit)
        {
            if (row["TagTarget"].IsString)
                return [ReadStored<MaintenanceTrack>(row["TagTarget"])];
            if (status.Phase == BatchPublicationPhase.Accepted && ReadMaintenanceTracks(status.LibraryId, [edit.TrackId]) is
                HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var tracks } && tracks.Single().TrackRevision == edit.ExpectedRevision)
                return tracks;
        }
        if (change is ApprovedBatchChange.Maintenance maintenance)
        {
            var plan = database.GetCollection<BsonDocument>(MaintenancePlans).FindById(maintenance.Plan.PlanId);
            if (plan is not null && plan["BatchOperationId"].IsGuid && plan["BatchOperationId"].AsGuid == status.OperationId &&
                plan["LibraryId"].AsGuid == status.LibraryId && plan["OriginInstanceId"].AsGuid == status.OriginInstanceId)
            {
                if (maintenance.Plan.Kind == LibraryMaintenanceKind.RemoveAudio)
                    audioFiles = ReadAudioRemovalFiles(plan);
                return [.. ReadStored<MaintenanceTrack[]>(plan["Targets"])];
            }
        }
        return [];
    }

    /// <summary>Revalidates the exact scoped application projection without requiring an unrelated feed head to be unchanged.</summary>
    private bool TargetsCurrent(NativeBatchWork work) => work.Entries.All(entry => entry.Approved.Change switch
    {
        ApprovedBatchChange.TrackTags edit => entry.Targets is [{ } target] && target.TrackId == edit.TrackId && target.TrackRevision == edit.ExpectedRevision &&
            SameMaintenanceTracks(ReadMaintenanceTracks(work.Status.LibraryId, [edit.TrackId]), entry.Targets),
        ApprovedBatchChange.Maintenance maintenance => !entry.Targets.IsEmpty &&
            SameMaintenanceTracks(ReadMaintenanceTracks(work.Status.LibraryId, maintenance.Plan.TrackIds), entry.Targets),
        _ => true
    });

    /// <summary>Rejects ambiguous artist projections before starting any native mutations.</summary>
    private bool TagProjectionAvailable(NativeBatchWork work)
    {
        var artists = database.GetCollection<BsonDocument>("catalog_artists");
        return work.Entries.Select(entry => entry.Approved.Change).OfType<ApprovedBatchChange.TrackTags>()
            .Select(edit => Normalize(edit.Changes.GetValueOrDefault("artist"))).OfType<string>().Distinct(StringComparer.Ordinal)
            .All(name => FindCatalog(artists, work.Status.LibraryId, "Name", name).Where(row => Text(row, "Name") == name).Take(2).Count() < 2);
    }

    /// <summary>Commits one checkpoint only while the loaded journal still owns the native cursor.</summary>
    private Task<HostOutcome<bool>> WriteBatchCursorAsync(NativeBatchWork work, int position, bool attempted, CancellationToken token,
        Action<BsonDocument>? updateEntry = null) => ExecuteAsync<bool>(_ =>
    {
        var row = CurrentBatch(work, BatchPublicationPhase.Running);
        if (row is null || work.NextPosition > work.Entries.Length)
            return new HostOutcome<bool>.Success(false);
        if (updateEntry is not null)
        {
            var entries = database.GetCollection<BsonDocument>(ApprovedBatchEntries);
            var id = FormattableString.Invariant($"{row["_id"].AsString}/{work.NextPosition}");
            if (entries.FindById(id) is not { } journal)
                return new HostOutcome<bool>.Unavailable("library-batch-journal-unavailable");
            updateEntry(journal);
            entries.Update(journal);
        }
        row["NextPosition"] = position;
        row["EntryAttempted"] = attempted;
        BatchJournals(database).Update(row);
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <summary>Matches the persisted checkpoint and phase before making progress.</summary>
    private BsonDocument? CurrentBatch(NativeBatchWork work, BatchPublicationPhase phase)
    {
        var status = work.Status;
        var row = BatchJournals(database).FindById(OperationId(new(status.OriginInstanceId, status.OperationId)));
        return row is not null && row["LibraryId"].AsGuid == status.LibraryId && Text(row, "Phase") == phase.ToString() &&
            (phase == BatchPublicationPhase.Accepted || row["NextPosition"].AsInt32 == work.NextPosition && EntryAttempted(row) == work.EntryAttempted) ? row : null;
    }

    /// <summary>Keeps the queryable phase and replayable approval status consistent.</summary>
    private void SaveBatchPhase(BsonDocument row, ApprovedLibraryBatchStatus status)
    {
        row["Phase"] = status.Phase.ToString();
        row["Status"] = JsonSerializer.Serialize(status);
        BatchJournals(database).Update(row);
    }

    private static ILiteCollection<BsonDocument> BatchJournals(ILiteDatabase db) => db.GetCollection<BsonDocument>(ApprovedBatchPublications);

    private static bool EntryAttempted(BsonDocument row) => row["EntryAttempted"].IsBoolean && row["EntryAttempted"].AsBoolean;

    private static ImmutableArray<ApprovedBatchEntry> ApprovedEntries(NativeBatchWork work) => [.. work.Entries.Select(entry => entry.Approved)];

    private static IEnumerable<ApprovedBatchChange.Playlist> BatchPlaylists(NativeBatchWork work) =>
        work.Entries.Select(entry => entry.Approved.Change).OfType<ApprovedBatchChange.Playlist>();
}
