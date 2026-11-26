using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Projects editable catalog tags and admits one unresolved edit per track on the database lane.</summary>
/// <remarks>2026-11-24: Journal the revision-pinned tag patch and its stable receipt for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed partial class LiteDbHostPlaylistStore : ITrackTagStore
{
    private const string TrackTagEditKind = "TrackTagEdit";
    private const string TrackTagReceiptType = "AcceptedTrackTagEdit";

    /// <inheritdoc />
    Task<HostOutcome<TrackTagsSnapshot>> ITrackTagStore.ReadAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller,
        CancellationToken token) => OnLaneAsync<TrackTagsSnapshot>(() =>
        {
            if (!IsImportCaller(libraryId, caller))
                return new HostOutcome<TrackTagsSnapshot>.Forbidden();
            var row = ReadTagTrack(libraryId, trackId);
            return row is null ? new HostOutcome<TrackTagsSnapshot>.Rejected("library-track-unavailable") :
                new HostOutcome<TrackTagsSnapshot>.Success(new(trackId, row["Revision"].AsInt64, ProjectTrackTags(libraryId, row)));
        }, token);

    /// <inheritdoc />
    Task<HostOutcome<TrackTagEditReceipt>> ITrackTagStore.AcceptAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller,
        Guid operationId, long expectedRevision, ImmutableSortedDictionary<string, string?> changes, CancellationToken token) =>
        ExecuteAsync<TrackTagEditReceipt>(_ =>
        {
            if (!IsImportCaller(libraryId, caller))
                return new HostOutcome<TrackTagEditReceipt>.Forbidden();
            var track = ReadTagTrack(libraryId, trackId);
            if (track is null)
                return new HostOutcome<TrackTagEditReceipt>.Rejected("library-track-unavailable");

            var patch = JsonSerializer.Serialize(changes);
            var fingerprint = HashKey(string.Create(CultureInfo.InvariantCulture, $"track-tag-edit/v1/{libraryId:N}/{trackId:N}/{expectedRevision}/{patch}"));
            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            var key = OperationId(new(caller.InstanceId, operationId));
            if (receipts.FindById(key) is { } prior)
            {
                var receipt = prior["Receipt"].AsDocument;
                return Text(prior, "IntentFingerprint") == fingerprint && Text(receipt, "Type") == TrackTagReceiptType &&
                    receipt["Version"].IsInt32 && receipt["Version"].AsInt32 == 1
                    ? new HostOutcome<TrackTagEditReceipt>.Success(ReadStored<TrackTagEditReceipt>(receipt["Result"]))
                    : new HostOutcome<TrackTagEditReceipt>.Rejected("operation-intent-mismatch", Conflict: true);
            }

            var actualRevision = track["Revision"].AsInt64;
            if (actualRevision != expectedRevision)
                return new HostOutcome<TrackTagEditReceipt>.Rejected("stale-track-tags", actualRevision, Conflict: true);
            if (!track["BeetsTrackId"].IsNumber || track["BeetsTrackId"].AsInt64 <= 0 || Text(track, "RelativePath") is not { Length: > 0 } path)
                return new HostOutcome<TrackTagEditReceipt>.Rejected("tag-native-track-unavailable");

            var jobs = database.GetCollection<BsonDocument>(LibraryJobs);
            if (PendingTrackTagJobs(jobs, libraryId, trackId).Any())
                return new HostOutcome<TrackTagEditReceipt>.Rejected("track-tag-edit-pending", actualRevision, Conflict: true);

            var result = new TrackTagEditReceipt(operationId, Guid.NewGuid(), libraryId, 1);
            jobs.Insert(new BsonDocument
            {
                ["_id"] = result.JobId, ["LibraryId"] = libraryId, ["OriginInstanceId"] = caller.InstanceId,
                ["OperationId"] = operationId, ["Kind"] = TrackTagEditKind, ["Status"] = nameof(ImportJobPhase.Accepted),
                ["Revision"] = result.Revision, ["TrackId"] = trackId, ["ExpectedTrackRevision"] = expectedRevision,
                ["BeetsTrackId"] = track["BeetsTrackId"], ["RelativePath"] = path, ["TagChanges"] = patch,
                ["TagBaseline"] = JsonSerializer.Serialize(ProjectTrackTags(libraryId, track))
            });
            receipts.Insert(new BsonDocument
            {
                ["_id"] = key, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = operationId, ["IntentFingerprint"] = fingerprint,
                ["Receipt"] = new BsonDocument { ["Type"] = TrackTagReceiptType, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<TrackTagEditReceipt>.Success(result);
        }, token);

    private BsonDocument? ReadTagTrack(Guid libraryId, Guid trackId) =>
        CatalogRow(database.GetCollection<BsonDocument>("catalog_tracks"), trackId, libraryId) is { } row &&
        row["Revision"].IsNumber && row["Revision"].AsInt64 > 0 ? row : null;

    private static IEnumerable<BsonDocument> PendingTrackTagJobs(ILiteCollection<BsonDocument> jobs, Guid libraryId, Guid trackId) =>
        jobs.Find(Query.And(Query.EQ("LibraryId", libraryId), Query.EQ("Kind", TrackTagEditKind), Query.EQ("TrackId", trackId),
            Query.Or(Query.In("Status", new BsonArray(nameof(ImportJobPhase.Accepted), nameof(ImportJobPhase.Running))),
                Query.And(Query.EQ("Status", nameof(ImportJobPhase.Failed)), Query.EQ("NativeAttempted", true)))));

    private ImmutableDictionary<string, string> ProjectTrackTags(Guid libraryId, BsonDocument track)
    {
        var artists = database.GetCollection<BsonDocument>("catalog_artists");
        var album = CatalogRow(database.GetCollection<BsonDocument>("catalog_albums"), track["AlbumId"], libraryId);
        var artist = CatalogRow(artists, track["ArtistId"], libraryId);
        var albumArtist = album is null ? null : CatalogRow(artists, album["AlbumArtistId"], libraryId);
        var tags = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        Add("title", Text(track, "Title"));
        Add("artist", Text(artist, "Name"));
        Add("albumartist", track.ContainsKey("TagAlbumArtist") ? Text(track, "TagAlbumArtist") : Text(albumArtist, "Name"));
        Add("album", track.ContainsKey("TagAlbum") ? Text(track, "TagAlbum") : Text(album, "Title"));
        Add("genre", Text(track, "Genre"));
        Add("year", Number(track["Year"]));
        Add("disc", Number(track["DiscNumber"]));
        Add("track", Number(track["TrackNumber"]));
        return tags.ToImmutable();

        void Add(string field, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                tags.Add(field, value);
        }
    }

    private static string? Number(BsonValue value) => value.IsNumber && value.AsInt64 > 0
        ? value.AsInt64.ToString(CultureInfo.InvariantCulture) : null;
}
