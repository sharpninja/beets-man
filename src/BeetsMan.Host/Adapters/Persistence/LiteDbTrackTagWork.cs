using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Persists the native tag write checkpoint and commits verified edits with their peer revision.</summary>
/// <remarks>2026-11-24: Recover exactly one track edit for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
public sealed partial class LiteDbHostPlaylistStore : ITrackTagWorkStore
{
    /// <inheritdoc />
    Task<HostOutcome<TrackTagWork?>> ITrackTagWorkStore.ReadNextTrackTagEditAsync(Guid libraryId, CancellationToken token) => OnLaneAsync<TrackTagWork?>(() =>
    {
        var pending = database.GetCollection<BsonDocument>(LibraryJobs).Find(Query.And(Query.EQ("LibraryId", libraryId),
            Query.EQ("Kind", TrackTagEditKind), Query.In("Status", new BsonArray(nameof(ImportJobPhase.Accepted), nameof(ImportJobPhase.Running)))));
        var first = pending.OrderByDescending(WasNativeAttempted).ThenBy(row => row["_id"].AsGuid).FirstOrDefault();
        return new HostOutcome<TrackTagWork?>.Success(first is null ? null : ReadTrackTagWork(first));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<TrackTagWork?>> ITrackTagWorkStore.BeginTrackTagWriteAsync(TrackTagWork work, CancellationToken token) => ExecuteAsync<TrackTagWork?>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentTrackTagWork(jobs, work);
        if (row is null || WasNativeAttempted(row))
            return new HostOutcome<TrackTagWork?>.Success(null);
        if (CurrentTagTrack(work) is null)
        {
            SaveLibraryJobFailure(jobs, row, "stale-track-tags", false, false);
            return new HostOutcome<TrackTagWork?>.Success(null);
        }
        row["Status"] = nameof(ImportJobPhase.Running);
        row["NativeAttempted"] = true;
        row["Revision"] = checked(work.Job.Revision + 1);
        jobs.Update(row);
        return new HostOutcome<TrackTagWork?>.Success(ReadTrackTagWork(row));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> ITrackTagWorkStore.FailTrackTagEditAsync(TrackTagWork work, string code, bool retryable, bool knownNotStarted,
        CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentTrackTagWork(jobs, work);
        if (row is null)
            return new HostOutcome<bool>.Success(false);
        SaveLibraryJobFailure(jobs, row, code, retryable, knownNotStarted);
        return new HostOutcome<bool>.Success(true);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<bool>> ITrackTagWorkStore.PublishTrackTagEditAsync(TrackTagWork work, CancellationToken token) => ExecuteAsync<bool>(_ =>
    {
        var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
        var row = FindCurrentTrackTagWork(jobs, work);
        if (row is null)
            return new HostOutcome<bool>.Success(false);
        var track = CurrentTagTrack(work);
        if (track is null)
        {
            SaveLibraryJobFailure(jobs, row, "stale-track-tags", false, false);
            return new HostOutcome<bool>.Success(true);
        }

        var library = work.Job.LibraryId;
        var head = ReadFeedHead(database, library);
        var revision = head["CurrentRevision"].AsInt64;
        var tags = ProjectTrackTags(library, track);
        // A verified patch that already matches the projection only needs a job checkpoint.
        if (work.Changes.Any(tag => Normalize(tag.Value) != tags.GetValueOrDefault(tag.Key)))
        {
            revision = checked(revision + 1);
            var changes = new BsonArray();
            if (!ApplyTagProjection(library, track, work.Changes, revision, changes))
            {
                SaveLibraryJobFailure(jobs, row, "tag-catalog-artist-ambiguous", true, false);
                return new HostOutcome<bool>.Success(true);
            }
            WriteCatalogChange(database.GetCollection<BsonDocument>("catalog_tracks"), track, "Track", revision, changes);
            AppendFeed(database, head, revision, new(row["OriginInstanceId"].AsGuid, row["OperationId"].AsGuid), changes);
        }
        row["Status"] = nameof(ImportJobPhase.Published);
        row["Revision"] = checked(work.Job.Revision + 1);
        row["PublishedRevision"] = Math.Max(revision, work.TrackRevision);
        jobs.Update(row);
        return new HostOutcome<bool>.Success(true);
    }, token);

    private BsonDocument? CurrentTagTrack(TrackTagWork work) => ReadTagTrack(work.Job.LibraryId, work.TrackId) is { } row &&
        row["Revision"].AsInt64 == work.TrackRevision && row["BeetsTrackId"].AsInt64 == work.BeetsTrackId &&
        ImportCatalogPublication.PathComparer.Equals(Text(row, "RelativePath"), work.RelativePath) ? row : null;

    private static BsonDocument? FindCurrentTrackTagWork(ILiteCollection<BsonDocument> jobs, TrackTagWork work) =>
        FindScopedLibraryJob(jobs, work.Job.LibraryId, work.Job.JobId) is { } row && Text(row, "Kind") == TrackTagEditKind &&
        ReadImportPhase(row) == work.Job.Phase && row["Revision"].AsInt64 == work.Job.Revision ? row : null;

    private static TrackTagWork ReadTrackTagWork(BsonDocument row) => new(ProjectImportJob(row), row["TrackId"].AsGuid,
        row["ExpectedTrackRevision"].AsInt64, row["BeetsTrackId"].AsInt64, row["RelativePath"].AsString,
        ReadStored<ImmutableDictionary<string, string>>(row["TagBaseline"]), ReadStored<ImmutableDictionary<string, string?>>(row["TagChanges"]),
        WasNativeAttempted(row));

    /// <summary>Stores a recoverable job failure and preserves an uncertain native attempt for FR-BEETS-001#ac-recover.</summary>
    private static void SaveLibraryJobFailure(ILiteCollection<BsonDocument> jobs, BsonDocument row, string code, bool retryable, bool knownNotStarted)
    {
        row["FailedFromPhase"] = row["Status"];
        row["Status"] = nameof(ImportJobPhase.Failed);
        row["FailureCode"] = code;
        row["Retryable"] = retryable;
        row["Revision"] = checked(row["Revision"].AsInt64 + 1);
        if (knownNotStarted)
            row["NativeAttempted"] = false;
        jobs.Update(row);
    }

    private bool ApplyTagProjection(Guid libraryId, BsonDocument track, ImmutableDictionary<string, string?> patch, long revision, BsonArray changes)
    {
        if (patch.TryGetValue("artist", out var name))
        {
            track["ArtistId"] = BsonValue.Null;
            if (Normalize(name) is { } artistName)
            {
                var artists = database.GetCollection<BsonDocument>("catalog_artists");
                var matches = FindCatalog(artists, libraryId, "Name", artistName).Where(row => Text(row, "Name") == artistName).Take(2).ToArray();
                if (matches.Length > 1)
                    return false;
                var artist = matches.SingleOrDefault();
                if (artist is null)
                {
                    artist = NewCatalogRow(libraryId);
                    artist["Name"] = artistName;
                    artist["SortName"] = artistName;
                    WriteCatalogChange(artists, artist, "Artist", revision, changes);
                }
                track["ArtistId"] = artist["_id"];
            }
        }
        SetText("title", "Title");
        SetText("genre", "Genre");
        SetText("album", "TagAlbum");
        SetText("albumartist", "TagAlbumArtist");
        SetNumber("year", "Year");
        SetNumber("disc", "DiscNumber");
        SetNumber("track", "TrackNumber");
        return true;

        void SetText(string tag, string field)
        {
            if (patch.TryGetValue(tag, out var value))
                track[field] = Normalize(value) is { } text ? new BsonValue(text) : BsonValue.Null;
        }

        void SetNumber(string tag, string field)
        {
            if (patch.TryGetValue(tag, out var value))
                track[field] = value is null ? BsonValue.Null : new BsonValue(int.Parse(value, CultureInfo.InvariantCulture));
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
