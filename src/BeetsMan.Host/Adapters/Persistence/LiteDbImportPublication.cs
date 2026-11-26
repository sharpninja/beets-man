using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Publishes a reconciled import using the host's catalog and shared revision feed transaction.</summary>
/// <remarks>2026-11-24: Keep catalog entities, job completion, feed and delivery atomic for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
/// <remarks>2026-11-24: Keep observed disc and year in the track projection for FR-BEETS-001#ac-edit.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IImportCatalogPublicationStore
{
    /// <inheritdoc />
    Task<HostOutcome<ImportJobSnapshot>> IImportCatalogPublicationStore.CommitImportPublicationAsync(ReconciledImportAlbum observed,
        string fingerprint, CancellationToken token) => ExecuteAsync<ImportJobSnapshot>(_ =>
        {
            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            var job = FindScopedImportJob(jobs, observed.LibraryId, observed.JobId);
            if (job is null || !job["UploadId"].IsGuid || job["UploadId"].AsGuid != observed.UploadId ||
                !job["ApprovedChoice"].IsDocument || !MatchesApproval(job["ApprovedChoice"].AsDocument, observed))
                return new HostOutcome<ImportJobSnapshot>.Rejected("import-publication-scope-mismatch");

            var phase = ReadImportPhase(job);
            var revision = job["Revision"].AsInt64;
            if (phase == ImportJobPhase.Published)
                return Text(job, "PublicationFingerprint") == fingerprint
                    ? new HostOutcome<ImportJobSnapshot>.Success(ProjectImportJob(job))
                    : new HostOutcome<ImportJobSnapshot>.Rejected("import-publication-intent-mismatch", revision, Conflict: true);
            if (phase != ImportJobPhase.Running || revision != observed.JobRevision)
                return new HostOutcome<ImportJobSnapshot>.Rejected("stale-import-publication", revision, Conflict: true);

            var origin = job["OriginInstanceId"].AsGuid;
            var upload = database.GetCollection<BsonDocument>(ImportUploads).FindById(UploadKey(observed.LibraryId, origin, observed.UploadId));
            if (upload is null || !upload["JobId"].IsGuid || upload["JobId"].AsGuid != observed.JobId ||
                ReadUpload(upload).Files.Length != observed.Tracks.Length)
                return new HostOutcome<ImportJobSnapshot>.Rejected("import-reconciliation-incomplete");

            var head = ReadFeedHead(database, observed.LibraryId);
            var catalogRevision = checked(head["CurrentRevision"].AsInt64 + 1);
            var changes = new BsonArray();
            if (!TryProjectImportAlbum(observed, catalogRevision, changes))
                return new HostOutcome<ImportJobSnapshot>.Rejected("import-catalog-identity-conflict", Conflict: true);
            AppendFeed(database, head, catalogRevision, new(origin, observed.ChoiceOperationId), changes);
            job["Status"] = nameof(ImportJobPhase.Published);
            job["Revision"] = checked(revision + 1);
            job["PublishedRevision"] = catalogRevision;
            job["PublicationFingerprint"] = fingerprint;
            jobs.Update(job);
            return new HostOutcome<ImportJobSnapshot>.Success(ProjectImportJob(job));
        }, token);

    private bool TryProjectImportAlbum(ReconciledImportAlbum observed, long revision, BsonArray changes)
    {
        var library = observed.LibraryId;
        var artists = database.GetCollection<BsonDocument>("catalog_artists");
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var name in observed.Tracks.Select(track => track.Artist).Prepend(observed.AlbumArtist).OfType<string>()
                     .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            var matches = FindCatalog(artists, library, "Name", name).Where(row => Text(row, "Name") == name).Take(2).ToArray();
            if (matches.Length > 1)
                return false;
            var artist = matches.SingleOrDefault();
            if (artist is null)
            {
                artist = NewCatalogRow(library);
                artist["Name"] = name;
                artist["SortName"] = name;
                WriteCatalogChange(artists, artist, "Artist", revision, changes);
            }
            ids.Add(name, artist["_id"].AsGuid);
        }

        var albums = database.GetCollection<BsonDocument>("catalog_albums");
        var existingAlbums = FindCatalog(albums, library, "BeetsAlbumId", observed.BeetsAlbumId).Take(2).ToArray();
        if (existingAlbums.Length > 1)
            return false;
        var album = existingAlbums.SingleOrDefault() ?? NewCatalogRow(library);
        album["BeetsAlbumId"] = observed.BeetsAlbumId;
        album["Title"] = observed.Title;
        album["SortTitle"] = observed.Title;
        album["AlbumArtistId"] = ArtistId(observed.AlbumArtist);
        WriteCatalogChange(albums, album, "Album", revision, changes);

        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        foreach (var observedTrack in observed.Tracks)
        {
            if (!TryResolveImportTrack(tracks, library, observedTrack, out var track))
                return false;
            track["BeetsTrackId"] = observedTrack.BeetsTrackId;
            track["AlbumId"] = album["_id"];
            track["ArtistId"] = ArtistId(observedTrack.Artist);
            track["RelativePath"] = observedTrack.RelativePath;
            track["Title"] = observedTrack.Title;
            track["Genre"] = observedTrack.Genre;
            track["TrackNumber"] = observedTrack.TrackNumber is int number ? new BsonValue(number) : BsonValue.Null;
            track["DiscNumber"] = observedTrack.DiscNumber is int disc ? new BsonValue(disc) : BsonValue.Null;
            track["Year"] = observedTrack.Year is int year ? new BsonValue(year) : BsonValue.Null;
            track["DurationTicks"] = observedTrack.DurationTicks is long ticks ? new BsonValue(ticks) : BsonValue.Null;
            WriteCatalogChange(tracks, track, "Track", revision, changes);
        }
        return true;

        BsonValue ArtistId(string? name) => name is not null && ids.TryGetValue(name, out var id) ? new BsonValue(id) : BsonValue.Null;
    }

    private static bool TryResolveImportTrack(ILiteCollection<BsonDocument> tracks, Guid library, ObservedImportTrack observed, out BsonDocument track)
    {
        var native = FindCatalog(tracks, library, "BeetsTrackId", observed.BeetsTrackId).Take(2).ToArray();
        var paths = FindCatalog(tracks, library, "RelativePath", observed.RelativePath)
            .Where(row => ImportCatalogPublication.PathComparer.Equals(Text(row, "RelativePath"), observed.RelativePath)).Take(2).ToArray();
        var nativeRow = native.FirstOrDefault();
        var pathRow = paths.FirstOrDefault();
        track = nativeRow ?? pathRow ?? NewCatalogRow(library);
        return native.Length <= 1 && paths.Length <= 1 &&
            (nativeRow is null || pathRow is null || nativeRow["_id"].AsGuid == pathRow["_id"].AsGuid) &&
            (pathRow is null || !pathRow["BeetsTrackId"].IsNumber || pathRow["BeetsTrackId"].AsInt64 == observed.BeetsTrackId);
    }

    private static bool MatchesApproval(BsonDocument choice, ReconciledImportAlbum observed) =>
        choice["OperationId"].IsGuid && choice["OperationId"].AsGuid == observed.ChoiceOperationId && (Text(choice, "Decision") switch
        {
            nameof(ImportChoiceKind.KeepTags) => observed.ProviderKey is null,
            nameof(ImportChoiceKind.UseMatch) => observed.ProviderKey is not null && observed.ProviderKey == Text(choice, "ProviderKey"),
            _ => false
        });

    private static IEnumerable<BsonDocument> FindCatalog(ILiteCollection<BsonDocument> collection, Guid libraryId, string field, BsonValue value) =>
        collection.Find(Query.And(Query.EQ("LibraryId", libraryId), Query.EQ(field, value)));

    private static BsonDocument NewCatalogRow(Guid libraryId) => new() { ["_id"] = Guid.NewGuid(), ["LibraryId"] = libraryId };

    private static void WriteCatalogChange(ILiteCollection<BsonDocument> collection, BsonDocument entity, string kind, long revision, BsonArray changes)
    {
        entity["Revision"] = revision;
        collection.Upsert(entity);
        changes.Add(FeedUpsert(kind, entity));
    }
}
