using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Projects the authoritative revision journal and commits independent peer acknowledgements.</summary>
/// <remarks>2026-11-24: Preserve the fixed page window, stable IDs and recipient scope for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IHostCatalogReplication
{
    /// <inheritdoc />
    Task<HostOutcome<CatalogFeedPage>> IHostCatalogReplication.ReadCatalogChangesAsync(Guid library, AuthenticatedHostInstance caller,
        long after, long? upper, int limit, CancellationToken token) => OnLaneAsync<CatalogFeedPage>(() =>
    {
        if (!IsPublicationCaller(library, caller))
            return new HostOutcome<CatalogFeedPage>.Forbidden();
        if (after < 0 || limit is < 1 or > CatalogFeedProtocol.MaximumPageSize || upper < after)
            return new HostOutcome<CatalogFeedPage>.Rejected("invalid-catalog-window");
        var head = ReadFeedHead(database, library);
        var current = head["CurrentRevision"].AsInt64;
        var fixedUpper = upper ?? current;
        if (after > current || fixedUpper > current)
            return new HostOutcome<CatalogFeedPage>.Rejected("catalog-cursor-ahead", current, Conflict: true);
        if (head["MinRetainedRevision"].IsNumber && after < head["MinRetainedRevision"].AsInt64)
            return new HostOutcome<CatalogFeedPage>.Rejected("catalog-snapshot-required", current, Conflict: true);
        var count = (int)Math.Min(limit, fixedUpper - after);
        var result = ImmutableArray.CreateBuilder<CatalogFeedRevision>(count);
        var revisions = database.GetCollection<BsonDocument>("revision_changes");
        for (var offset = 1; offset <= count; offset++)
        {
            token.ThrowIfCancellationRequested();
            var revision = after + offset;
            var row = revisions.FindById(FeedId(library, revision));
            if (row is null || ReadReplicaRevision(row, library, revision) is not { } change)
                return new HostOutcome<CatalogFeedPage>.Unavailable("catalog-revision-unavailable");
            result.Add(change);
        }
        var page = new CatalogFeedPage(library, caller.InstanceId, after, after + count, fixedUpper, result.MoveToImmutable());
        return CatalogFeedProtocol.IsValid(page) ? new HostOutcome<CatalogFeedPage>.Success(page) :
            new HostOutcome<CatalogFeedPage>.Unavailable("catalog-revision-unavailable");
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<CatalogNotifications>> IHostCatalogReplication.ReadCatalogNotificationsAsync(Guid library, AuthenticatedHostInstance caller,
        int limit, CancellationToken token) => OnLaneAsync<CatalogNotifications>(() =>
    {
        if (!IsPublicationCaller(library, caller))
            return new HostOutcome<CatalogNotifications>.Forbidden();
        if (limit is < 1 or > CatalogFeedProtocol.MaximumPageSize)
            return new HostOutcome<CatalogNotifications>.Rejected("invalid-catalog-notifications-limit");
        return new HostOutcome<CatalogNotifications>.Success(new(library, caller.InstanceId,
            [.. PendingCatalogNotifications(database.GetCollection<BsonDocument>("notification_deliveries"), library, caller.InstanceId)
                .Select(row => row["Revision"].AsInt64).Order().Take(limit)]));
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<CatalogAcknowledgementReceipt>> IHostCatalogReplication.AcknowledgeCatalogAsync(Guid library, AuthenticatedHostInstance caller,
        CatalogAcknowledgement request, CancellationToken token) => ExecuteAsync<CatalogAcknowledgementReceipt>(_ =>
    {
        if (!IsPublicationCaller(library, caller) || request.RecipientInstanceId != caller.InstanceId)
            return new HostOutcome<CatalogAcknowledgementReceipt>.Forbidden();
        var head = ReadFeedHead(database, library)["CurrentRevision"].AsInt64;
        if (request.ThroughRevision < 0 || request.ThroughRevision > head)
            return new HostOutcome<CatalogAcknowledgementReceipt>.Rejected("invalid-catalog-acknowledgement", head, Conflict: true);
        var notifications = database.GetCollection<BsonDocument>("notification_deliveries");
        foreach (var row in PendingCatalogNotifications(notifications, library, caller.InstanceId)
                     .Where(row => row["Revision"].AsInt64 <= request.ThroughRevision).ToArray())
        {
            token.ThrowIfCancellationRequested();
            row["Status"] = "Acknowledged";
            notifications.Update(row);
        }
        return new HostOutcome<CatalogAcknowledgementReceipt>.Success(new(library, caller.InstanceId, request.ThroughRevision));
    }, token);

    private static IEnumerable<BsonDocument> PendingCatalogNotifications(ILiteCollection<BsonDocument> table, Guid library, Guid recipient) =>
        table.Find(Query.And(Query.EQ("LibraryId", library), Query.EQ("RecipientInstanceId", recipient), Query.EQ("Status", "Pending")));

    private static CatalogFeedRevision? ReadReplicaRevision(BsonDocument row, Guid library, long revision)
    {
        if (row["LibraryId"].AsGuid != library || row["Revision"].AsInt64 != revision || !row["Changes"].IsArray)
            return null;
        var changes = ImmutableArray.CreateBuilder<CatalogFeedChange>(row["Changes"].AsArray.Count);
        foreach (var entry in row["Changes"].AsArray)
        {
            if (!entry.IsDocument || ReadReplicaChange(entry.AsDocument, library, revision) is not { } decoded)
                return null;
            changes.Add(decoded);
        }
        return new(revision, row["OriginInstanceId"].AsGuid, row["OperationId"].AsGuid, changes.MoveToImmutable());
    }

    private static CatalogFeedChange? ReadReplicaChange(BsonDocument change, Guid library, long revision)
    {
        if (!change["Version"].IsInt32 || change["Version"].AsInt32 != 1 || !change["Payload"].IsDocument ||
            !Enum.TryParse<CatalogEntityKind>(Text(change, "Kind"), out var kind))
            return null;
        var row = change["Payload"].AsDocument;
        if (row["LibraryId"].AsGuid != library || !row["_id"].IsGuid || !row["Revision"].IsNumber)
            return null;
        var id = row["_id"].AsGuid;
        var operation = Text(change, "Operation");
        if (operation == "Delete" && row["Revision"].AsInt64 == revision)
            return new(kind, CatalogEntityOperation.Delete, id);
        if (operation != "Upsert" || (kind != CatalogEntityKind.Playlist && row["Revision"].AsInt64 != revision))
            return null;
        return ProjectReplicaUpsert(kind, row);
    }

    /// <summary>Projects fields shared by the live feed and captured catalog for FR-RECORDS-001#ac-replicate.</summary>
    private static CatalogFeedChange? ProjectReplicaUpsert(CatalogEntityKind kind, BsonDocument row)
    {
        var id = row["_id"].AsGuid;
        return kind switch
        {
            CatalogEntityKind.Artist when Text(row, "Name") is { } name => new(kind, CatalogEntityOperation.Upsert, id,
                new ReplicaArtist(name, Text(row, "SortName"))),
            CatalogEntityKind.Album when Text(row, "Title") is { } title => new(kind, CatalogEntityOperation.Upsert, id,
                new ReplicaAlbum(title, Text(row, "SortTitle"), Id("AlbumArtistId"))),
            CatalogEntityKind.Track when Text(row, "Title") is { } title && Text(row, "RelativePath") is { } path =>
                new(kind, CatalogEntityOperation.Upsert, id, new ReplicaTrack(title, path, Id("ArtistId"), Id("AlbumId"), Text(row, "SortTitle"),
                    Text(row, "Genre"), Number("TrackNumber"), Number("DiscNumber"), Number("Year"), Number("Rating"),
                    row["DurationTicks"].IsNumber ? row["DurationTicks"].AsInt64 : null, row.ContainsKey("TagAlbum"), Text(row, "TagAlbum"),
                    row.ContainsKey("TagAlbumArtist"), Text(row, "TagAlbumArtist"))),
            CatalogEntityKind.Playlist when Text(row, "Name") is { } name && row["Entries"].IsArray &&
                row["Entries"].AsArray.All(value => value.IsDocument && value.AsDocument["EntryId"].IsGuid && value.AsDocument["TrackId"].IsGuid) =>
                new(kind, CatalogEntityOperation.Upsert, id, new ReplicaPlaylist(name, row["Revision"].AsInt64,
                    [.. row["Entries"].AsArray.Select(value => new PlaylistEntryData(value.AsDocument["EntryId"].AsGuid, value.AsDocument["TrackId"].AsGuid))])),
            CatalogEntityKind.Favorite when Id("TrackId") is Guid track => new(kind, CatalogEntityOperation.Upsert, id, new ReplicaFavorite(track)),
            CatalogEntityKind.Play when Id("TrackId") is Guid track && row["PlayedAtUtcTicks"].IsInt64 &&
                row["PlayedAtUtcTicks"].AsInt64 > 0 && row["PlayedAtUtcTicks"].AsInt64 <= DateTime.MaxValue.Ticks =>
                new(kind, CatalogEntityOperation.Upsert, id, new ReplicaPlay(track, new(row["PlayedAtUtcTicks"].AsInt64, TimeSpan.Zero))),
            CatalogEntityKind.SharedDefault when Text(row, "PreferenceKey") is { } preference && row["RegistryVersion"].IsInt32 &&
                (row["DurationTicks"].IsInt64 || row["Flag"].IsBoolean || row["Text"].IsString) =>
                new(kind, CatalogEntityOperation.Upsert, id, new ReplicaSharedDefault(preference, row["RegistryVersion"].AsInt32,
                    row["DurationTicks"].IsInt64 ? row["DurationTicks"].AsInt64 : null,
                    row["Flag"].IsBoolean ? row["Flag"].AsBoolean : null, Text(row, "Text"))),
            _ => null
        };

        Guid? Id(string field) => row[field].IsGuid ? row[field].AsGuid : null;
        int? Number(string field) => row[field].IsNumber ? row[field].AsInt32 : null;
    }
}
