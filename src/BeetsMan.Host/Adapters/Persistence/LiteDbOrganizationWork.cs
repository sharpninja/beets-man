using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the exact organization checkpoint and commits verified paths with peer revisions.</summary>
/// <remarks>2026-11-24: Recover organization without republishing or deleting source bytes for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IOrganizationWorkStore
{
    /// <inheritdoc />
    Task<HostOutcome<OrganizationWork?>> IOrganizationWorkStore.ReadNextOrganizationAsync(Guid library, CancellationToken token) =>
        OnLaneAsync<OrganizationWork?>(() =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = jobs.Find(Query.And(Query.EQ("LibraryId", library), Query.EQ("Kind", MaintenanceJobKind),
                    Query.EQ("MaintenanceMode", nameof(LibraryMaintenanceKind.Organize)),
                    Query.In("Status", new BsonArray(nameof(ImportJobPhase.Accepted), nameof(ImportJobPhase.Running)))))
                .OrderByDescending(WasNativeAttempted).ThenBy(job => job["_id"].AsGuid).FirstOrDefault();
            if (row is null)
                return new HostOutcome<OrganizationWork?>.Success(null);
            var work = ReadOrganizationWork(row);
            return work is null ? new HostOutcome<OrganizationWork?>.Unavailable("organization-plan-unavailable") :
                new HostOutcome<OrganizationWork?>.Success(work);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<OrganizationWork?>> IOrganizationWorkStore.BeginOrganizationAsync(OrganizationWork work,
        ImmutableArray<OrganizationCopy> copies, CancellationToken token) => ExecuteAsync<OrganizationWork?>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentOrganization(jobs, work);
        if (row is null || work.Job.Phase != ImportJobPhase.Accepted)
            return new HostOutcome<OrganizationWork?>.Success(null);
        var library = work.Job.LibraryId;
        var current = ReadMaintenanceSelection(library, new(library, row["OriginInstanceId"].AsGuid), work.Plan.CatalogRevision, work.Plan.TrackIds);
        if (!SameMaintenanceTracks(current, work.Tracks) || !OrganizationManifestMatches(work.Plan, work.Tracks, copies) ||
            !DestinationsUnclaimed(library, copies.Select(copy => copy.Destination)))
        {
            SaveLibraryJobFailure(jobs, row, "stale-library-maintenance", false, false);
            return new HostOutcome<OrganizationWork?>.Success(null);
        }
        row["Status"] = nameof(ImportJobPhase.Running);
        row["NativeAttempted"] = true;
        row["OrganizationCopies"] = JsonSerializer.Serialize(copies);
        row["Revision"] = checked(work.Job.Revision + 1);
        jobs.Update(row);
        return new HostOutcome<OrganizationWork?>.Success(work with { Job = ProjectImportJob(row), Copies = copies });
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> IOrganizationWorkStore.FailOrganizationAsync(OrganizationWork work, string code, bool retryable, CancellationToken token) =>
        ExecuteAsync<bool>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var row = FindCurrentOrganization(jobs, work);
            if (row is null)
                return new HostOutcome<bool>.Success(false);
            SaveLibraryJobFailure(jobs, row, code, retryable, false);
            return new HostOutcome<bool>.Success(true);
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> IOrganizationWorkStore.PublishOrganizationAsync(OrganizationWork work, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentOrganization(jobs, work);
        if (row is null || work.Job.Phase != ImportJobPhase.Running)
            return new HostOutcome<bool>.Success(false);
        var library = work.Job.LibraryId;
        if (!SameMaintenanceTracks(ReadMaintenanceTracks(library, work.Plan.TrackIds), work.Tracks) ||
            !DestinationsUnclaimed(library, work.Copies.Select(copy => copy.Destination)))
        {
            SaveLibraryJobFailure(jobs, row, "organization-publication-baseline-changed", true, false);
            return new HostOutcome<bool>.Success(true);
        }
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        var head = ReadFeedHead(database, library);
        var revision = checked(head["CurrentRevision"].AsInt64 + 1);
        var changes = new BsonArray();
        foreach (var copy in work.Copies)
        {
            token.ThrowIfCancellationRequested();
            var track = tracks.FindById(copy.Track.TrackId);
            track["RelativePath"] = copy.Destination;
            WriteCatalogChange(tracks, track, "Track", revision, changes);
        }
        AppendFeed(database, head, revision, new(row["OriginInstanceId"].AsGuid, row["OperationId"].AsGuid), changes);
        row["Status"] = nameof(ImportJobPhase.Published);
        row["PublishedRevision"] = revision;
        row["Revision"] = checked(work.Job.Revision + 1);
        jobs.Update(row);
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <summary>Uses only the plan and manifest reserved by the exact origin and job.</summary>
    private OrganizationWork? ReadOrganizationWork(BsonDocument job)
    {
        if (!IsOrganizationJob(job) || !job["MaintenancePlanId"].IsGuid || WasNativeAttempted(job) != (ReadImportPhase(job) == ImportJobPhase.Running))
            return null;
        var planRow = database.GetCollection<BsonDocument>(MaintenancePlans).FindById(job["MaintenancePlanId"].AsGuid);
        if (planRow is null || planRow["JobId"] != job["_id"] || planRow["OriginInstanceId"] != job["OriginInstanceId"] || planRow["LibraryId"] != job["LibraryId"])
            return null;
        var plan = ReadStored<LibraryMaintenancePlan>(planRow["Plan"]);
        var tracks = ReadStored<MaintenanceTrack[]>(planRow["Targets"]).ToImmutableArray();
        if (plan.PlanId != job["MaintenancePlanId"].AsGuid || plan.LibraryId != job["LibraryId"].AsGuid || plan.Kind != LibraryMaintenanceKind.Organize ||
            plan.CatalogRevision != job["ExpectedRevision"].AsInt64 || !plan.AudioPaths.IsEmpty || !tracks.Select(track => track.TrackId).SequenceEqual(plan.TrackIds) ||
            plan.Moves.IsDefaultOrEmpty || !HostLibraryMaintenance.ValidMoves(tracks, plan.Moves))
            return null;
        var copies = job["OrganizationCopies"].IsString ? ReadStored<OrganizationCopy[]>(job["OrganizationCopies"]).ToImmutableArray() : [];
        return WasNativeAttempted(job) && !OrganizationManifestMatches(plan, tracks, copies) ? null : new(ProjectImportJob(job), plan, tracks, copies);
    }

    /// <summary>Checks the stored manifest against each approved source and destination in plan order.</summary>
    private static bool OrganizationManifestMatches(LibraryMaintenancePlan plan, ImmutableArray<MaintenanceTrack> tracks, ImmutableArray<OrganizationCopy> copies)
    {
        var selection = tracks.ToDictionary(track => track.TrackId);
        return !copies.IsDefault && copies.Length == plan.Moves.Length && copies.Zip(plan.Moves).All(pair =>
            selection.TryGetValue(pair.Second.TrackId, out var target) && pair.First.Track == target && pair.First.Destination == pair.Second.Destination &&
            new OrganizationFileStamp(pair.First.Size, pair.First.Sha256).IsValid());
    }

    /// <summary>Prevents two host catalog tracks from claiming the same approved destination.</summary>
    private bool DestinationsUnclaimed(Guid library, IEnumerable<string> destinations)
    {
        var paths = destinations.ToHashSet(ImportCatalogPublication.PathComparer);
        return paths.Count == 0 || !database.GetCollection<BsonDocument>("catalog_tracks").Find(Query.EQ("LibraryId", library))
            .Any(row => Text(row, "RelativePath") is { } path && paths.Contains(path));
    }

    /// <summary>Matches the active phase and revision of the same organization job before writing.</summary>
    private static BsonDocument? FindCurrentOrganization(ILiteCollection<BsonDocument> jobs, OrganizationWork work) =>
        FindScopedLibraryJob(jobs, work.Job.LibraryId, work.Job.JobId) is { } row && IsOrganizationJob(row) &&
        row["MaintenancePlanId"].AsGuid == work.Plan.PlanId && row["Revision"].AsInt64 == work.Job.Revision && ReadImportPhase(row) == work.Job.Phase ? row : null;

    /// <summary>Recognizes explicit organization jobs on the common retry route.</summary>
    private static bool IsOrganizationJob(BsonDocument row) => Text(row, "Kind") == MaintenanceJobKind &&
        Text(row, "MaintenanceMode") == nameof(LibraryMaintenanceKind.Organize) && row["AudioDeletionApproved"].IsBoolean && !row["AudioDeletionApproved"].AsBoolean;
}
