using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the execution checkpoint and publishes confirmed deletion of approved tracks.</summary>
/// <remarks>2026-11-24: Commit reconciled deletion, peer revision, and job status atomically for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
public sealed partial class LiteDbHostPlaylistStore : ICatalogRemovalWorkStore
{
    /// <inheritdoc />
    Task<HostOutcome<CatalogRemovalWork?>> ICatalogRemovalWorkStore.ReadNextCatalogRemovalAsync(Guid libraryId, CancellationToken token) =>
        OnLaneAsync<CatalogRemovalWork?>(() =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var pending = jobs.Find(Query.And(Query.EQ("LibraryId", libraryId), Query.EQ("Kind", MaintenanceJobKind),
                Query.In("MaintenanceMode", new BsonArray(nameof(LibraryMaintenanceKind.RemoveCatalog), nameof(LibraryMaintenanceKind.RemoveAudio))),
                Query.In("Status", new BsonArray(nameof(ImportJobPhase.Accepted), nameof(ImportJobPhase.Running)))));
            var row = pending.Where(IsRemovalJob).OrderByDescending(WasNativeAttempted).ThenBy(job => job["_id"].AsGuid).FirstOrDefault();
            if (row is null)
                return new HostOutcome<CatalogRemovalWork?>.Success(null);
            var work = ReadCatalogRemovalWork(row);
            return work is null ? new HostOutcome<CatalogRemovalWork?>.Unavailable("maintenance-catalog-plan-unavailable") :
                new HostOutcome<CatalogRemovalWork?>.Success(work);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<CatalogRemovalWork?>> ICatalogRemovalWorkStore.PrepareCatalogRemovalAsync(CatalogRemovalWork work, CancellationToken token) =>
        ExecuteAsync<CatalogRemovalWork?>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = FindCurrentCatalogRemoval(jobs, work);
            if (row is null)
                return new HostOutcome<CatalogRemovalWork?>.Success(null);
            var library = work.Job.LibraryId;
            var current = work.NativeAttempted ? ReadMaintenanceTracks(library, work.Plan.TrackIds) :
                ReadMaintenanceSelection(library, new(library, row["OriginInstanceId"].AsGuid), work.Plan.CatalogRevision, work.Plan.TrackIds);
            if (!SameMaintenanceTracks(current, work.Tracks))
            {
                SaveLibraryJobFailure(jobs, row, "stale-library-maintenance", false, false);
                return new HostOutcome<CatalogRemovalWork?>.Success(null);
            }
            if (work.Plan.Kind == LibraryMaintenanceKind.RemoveAudio && !AudioPathsAreExclusive(library, work.Tracks))
            {
                SaveLibraryJobFailure(jobs, row, "audio-removal-path-still-referenced", work.NativeAttempted, false);
                return new HostOutcome<CatalogRemovalWork?>.Success(null);
            }
            if (work.NativeAttempted)
                return new HostOutcome<CatalogRemovalWork?>.Success(work);

            row["Status"] = nameof(ImportJobPhase.Running);
            row["NativeAttempted"] = true;
            row["Revision"] = checked(work.Job.Revision + 1);
            jobs.Update(row);
            return new HostOutcome<CatalogRemovalWork?>.Success(work with { Job = ProjectImportJob(row), NativeAttempted = true });
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> ICatalogRemovalWorkStore.FailCatalogRemovalAsync(CatalogRemovalWork work, string code, bool retryable, CancellationToken token) =>
        ExecuteAsync<bool>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = FindCurrentCatalogRemoval(jobs, work);
            if (row is null)
                return new HostOutcome<bool>.Success(false);
            SaveLibraryJobFailure(jobs, row, code, retryable, false);
            return new HostOutcome<bool>.Success(true);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> ICatalogRemovalWorkStore.PublishCatalogRemovalAsync(CatalogRemovalWork work, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentCatalogRemoval(jobs, work);
        if (row is null || !work.NativeAttempted || work.Job.Phase != ImportJobPhase.Running)
            return new HostOutcome<bool>.Success(false);
        var library = work.Job.LibraryId;
        if (!SameMaintenanceTracks(ReadMaintenanceTracks(library, work.Plan.TrackIds), work.Tracks))
        {
            SaveLibraryJobFailure(jobs, row, "stale-library-maintenance", false, false);
            return new HostOutcome<bool>.Success(true);
        }

        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        var head = ReadFeedHead(database, library);
        var revision = checked(head["CurrentRevision"].AsInt64 + 1);
        var updates = new BsonArray();
        foreach (var track in work.Tracks)
        {
            if (!tracks.Delete(track.TrackId))
                return new HostOutcome<bool>.Unavailable("maintenance-catalog-delete-uncommitted");
            updates.Add(TrackTombstone(library, track.TrackId, revision));
        }
        AppendFeed(database, head, revision, new(row["OriginInstanceId"].AsGuid, row["OperationId"].AsGuid), updates);
        row["Status"] = nameof(ImportJobPhase.Published);
        row["Revision"] = checked(work.Job.Revision + 1);
        row["PublishedRevision"] = revision;
        jobs.Update(row);
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <summary>Creates the existing catalog track deletion record for FR-RECORDS-001#ac-replicate.</summary>
    private static BsonDocument TrackTombstone(Guid library, Guid track, long revision) => new()
    {
        ["Version"] = 1, ["Kind"] = "Track", ["Operation"] = "Delete",
        ["Payload"] = new BsonDocument { ["_id"] = track, ["LibraryId"] = library, ["Revision"] = revision }
    };

    private CatalogRemovalWork? ReadCatalogRemovalWork(BsonDocument job)
    {
        if (!IsRemovalJob(job) || !job["MaintenancePlanId"].IsGuid || WasNativeAttempted(job) != (ReadImportPhase(job) == ImportJobPhase.Running))
            return null;
        var planRow = database.GetCollection<BsonDocument>(MaintenancePlans).FindById(job["MaintenancePlanId"].AsGuid);
        if (planRow is null || !planRow["JobId"].IsGuid || planRow["JobId"] != job["_id"] || planRow["OriginInstanceId"] != job["OriginInstanceId"] ||
            planRow["LibraryId"] != job["LibraryId"])
            return null;
        var plan = ReadStored<LibraryMaintenancePlan>(planRow["Plan"]);
        var tracks = ReadStored<MaintenanceTrack[]>(planRow["Targets"]).ToImmutableArray();
        if (plan.PlanId != job["MaintenancePlanId"].AsGuid || plan.LibraryId != job["LibraryId"].AsGuid || plan.Kind.ToString() != Text(job, "MaintenanceMode") ||
            plan.CatalogRevision != job["ExpectedRevision"].AsInt64 || !plan.Moves.IsDefaultOrEmpty ||
            tracks.IsDefaultOrEmpty || plan.TrackIds.IsDefaultOrEmpty || !tracks.Select(target => target.TrackId).SequenceEqual(plan.TrackIds))
            return null;
        if (plan.Kind == LibraryMaintenanceKind.RemoveCatalog)
            return plan.AudioPaths.IsDefaultOrEmpty ? new(ProjectImportJob(job), plan, tracks, WasNativeAttempted(job)) : null;
        if (plan.AudioPaths.IsDefaultOrEmpty || !plan.AudioPaths.SequenceEqual(tracks.Select(track => track.RelativePath), ImportCatalogPublication.PathComparer))
            return null;
        return new(ProjectImportJob(job), plan, tracks, WasNativeAttempted(job), ReadAudioRemovalFiles(planRow));
    }

    private static bool SameMaintenanceTracks(HostOutcome<ImmutableArray<MaintenanceTrack>> current, ImmutableArray<MaintenanceTrack> expected) =>
        current is HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var observed } && observed.SequenceEqual(expected);

    private static BsonDocument? FindCurrentCatalogRemoval(ILiteCollection<BsonDocument> jobs, CatalogRemovalWork work) =>
        FindScopedLibraryJob(jobs, work.Job.LibraryId, work.Job.JobId) is { } row && IsRemovalJob(row) &&
        row["MaintenancePlanId"].AsGuid == work.Plan.PlanId && row["Revision"].AsInt64 == work.Job.Revision &&
        ReadImportPhase(row) == work.Job.Phase && WasNativeAttempted(row) == work.NativeAttempted ? row : null;

    /// <summary>Recognizes the exact admitted mode and requires a separate persisted approval to delete audio.</summary>
    private static bool IsRemovalJob(BsonDocument row) => Text(row, "Kind") == MaintenanceJobKind && row["AudioDeletionApproved"].IsBoolean &&
        Text(row, "MaintenanceMode") == (row["AudioDeletionApproved"].AsBoolean ? nameof(LibraryMaintenanceKind.RemoveAudio) : nameof(LibraryMaintenanceKind.RemoveCatalog));

    /// <summary>Rejects deletion when an unselected host catalog track refers to the same relative audio path.</summary>
    private bool AudioPathsAreExclusive(Guid library, ImmutableArray<MaintenanceTrack> targets)
    {
        if (targets.IsEmpty)
            return true;
        var selected = targets.Select(track => track.TrackId).ToHashSet();
        var paths = targets.Select(track => track.RelativePath).ToHashSet(ImportCatalogPublication.PathComparer);
        return paths.Count == targets.Length && !database.GetCollection<BsonDocument>("catalog_tracks").Find(Query.EQ("LibraryId", library))
            .Any(row => !selected.Contains(row["_id"].AsGuid) && Text(row, "RelativePath") is { } path && paths.Contains(path));
    }
}
