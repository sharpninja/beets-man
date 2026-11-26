using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Captures and pages one immutable catalog replacement per registered recipient.</summary>
/// <remarks>2026-11-24: Retain a consistent catalog through concurrent publication and restart for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string CatalogSnapshots = "catalog_replica_snapshots";
    private const string CatalogSnapshotRows = "catalog_replica_snapshot_rows";

    private static readonly (CatalogEntityKind Kind, string Table)[] SnapshotSources =
    [
        (CatalogEntityKind.Artist, "catalog_artists"), (CatalogEntityKind.Album, "catalog_albums"),
        (CatalogEntityKind.Track, "catalog_tracks"), (CatalogEntityKind.Playlist, "saved_playlists"),
        (CatalogEntityKind.Favorite, ListeningRecords), (CatalogEntityKind.Play, ListeningRecords), (CatalogEntityKind.SharedDefault, SharedPreferences)
    ];

    /// <inheritdoc />
    Task<HostOutcome<CatalogSnapshotPage>> IHostCatalogReplication.BeginCatalogSnapshotAsync(Guid library, AuthenticatedHostInstance caller,
        BeginCatalogSnapshot request, CancellationToken token) => ExecuteAsync<CatalogSnapshotPage>(_ =>
    {
        if (!IsPublicationCaller(library, caller) || request.RecipientInstanceId != caller.InstanceId)
            return new HostOutcome<CatalogSnapshotPage>.Forbidden();
        if (request.SnapshotId == Guid.Empty || request.ExpectedCursor < 0 || !ValidSnapshotLimit(request.Limit))
            return new HostOutcome<CatalogSnapshotPage>.Rejected("invalid-catalog-snapshot-request");

        var owner = SnapshotOwner(library, caller.InstanceId);
        var headers = database.GetCollection<BsonDocument>(CatalogSnapshots);
        var rows = database.GetCollection<BsonDocument>(CatalogSnapshotRows);
        if (headers.FindById(owner) is { } existing && existing["SnapshotId"].AsGuid == request.SnapshotId)
            return existing["ExpectedCursor"].AsInt64 == request.ExpectedCursor ? ReadSnapshotPage(rows, existing, 0, request.Limit, token) :
                new HostOutcome<CatalogSnapshotPage>.Rejected("catalog-snapshot-intent-mismatch", Conflict: true);

        var upper = ReadFeedHead(database, library)["CurrentRevision"].AsInt64;
        if (request.ExpectedCursor > upper)
            return new HostOutcome<CatalogSnapshotPage>.Rejected("catalog-cursor-ahead", upper, Conflict: true);
        rows.DeleteMany(Query.EQ("Owner", owner));
        var total = 0;
        foreach (var (kind, collection) in SnapshotSources)
        {
            foreach (var row in database.GetCollection<BsonDocument>(collection).Find(Query.EQ("LibraryId", library)).OrderBy(row => row["_id"].AsGuid))
            {
                token.ThrowIfCancellationRequested();
                if (collection == ListeningRecords && Text(row, "Kind") != kind.ToString())
                    continue;
                var revision = kind == CatalogEntityKind.Playlist ? upper : row["Revision"].AsInt64;
                var projected = IsLibraryRecordDeleted(row) ? new CatalogFeedChange(kind, CatalogEntityOperation.Delete, row["_id"].AsGuid) :
                    ProjectReplicaUpsert(kind, row);
                if (revision <= 0 || revision > upper || projected is null)
                    return new HostOutcome<CatalogSnapshotPage>.Unavailable("catalog-snapshot-record-unavailable");
                rows.Insert(new BsonDocument { ["_id"] = SnapshotPosition(owner, total), ["Owner"] = owner,
                    ["Payload"] = JsonSerializer.Serialize(new CatalogSnapshotRecord(revision, projected)) });
                total = checked(total + 1);
            }
        }
        var header = new BsonDocument { ["_id"] = owner, ["LibraryId"] = library, ["RecipientInstanceId"] = caller.InstanceId,
            ["SnapshotId"] = request.SnapshotId, ["ExpectedCursor"] = request.ExpectedCursor, ["UpperRevision"] = upper, ["TotalCount"] = total };
        headers.Upsert(header);
        return ReadSnapshotPage(rows, header, 0, request.Limit, token);
    }, token);

    /// <inheritdoc />
    Task<HostOutcome<CatalogSnapshotPage>> IHostCatalogReplication.ReadCatalogSnapshotAsync(Guid library, AuthenticatedHostInstance caller,
        Guid snapshotId, int offset, int limit, CancellationToken token) => OnLaneAsync<CatalogSnapshotPage>(() =>
    {
        if (!IsPublicationCaller(library, caller))
            return new HostOutcome<CatalogSnapshotPage>.Forbidden();
        if (snapshotId == Guid.Empty || offset < 0 || !ValidSnapshotLimit(limit))
            return new HostOutcome<CatalogSnapshotPage>.Rejected("invalid-catalog-snapshot-request");
        var header = database.GetCollection<BsonDocument>(CatalogSnapshots).FindById(SnapshotOwner(library, caller.InstanceId));
        return header is not null && header["SnapshotId"].AsGuid == snapshotId ?
            ReadSnapshotPage(database.GetCollection<BsonDocument>(CatalogSnapshotRows), header, offset, limit, token) :
            new HostOutcome<CatalogSnapshotPage>.Rejected("catalog-snapshot-unavailable", Conflict: true);
    }, token);

    private static HostOutcome<CatalogSnapshotPage> ReadSnapshotPage(ILiteCollection<BsonDocument> rows, BsonDocument header, int offset, int limit,
        CancellationToken token)
    {
        var total = header["TotalCount"].AsInt32;
        if (offset > total)
            return new HostOutcome<CatalogSnapshotPage>.Rejected("invalid-catalog-snapshot-offset");
        var count = Math.Min(limit, total - offset);
        var page = ImmutableArray.CreateBuilder<CatalogSnapshotRecord>(count);
        var owner = header["_id"].AsString;
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (rows.FindById(SnapshotPosition(owner, offset + index)) is not { } row)
                return new HostOutcome<CatalogSnapshotPage>.Unavailable("catalog-snapshot-record-unavailable");
            page.Add(ReadStored<CatalogSnapshotRecord>(row["Payload"]));
        }
        var result = new CatalogSnapshotPage(header["LibraryId"].AsGuid, header["RecipientInstanceId"].AsGuid, header["SnapshotId"].AsGuid,
            header["UpperRevision"].AsInt64, total, offset, page.MoveToImmutable());
        return CatalogSnapshotProtocol.IsValid(result) ? new HostOutcome<CatalogSnapshotPage>.Success(result) :
            new HostOutcome<CatalogSnapshotPage>.Unavailable("catalog-snapshot-record-unavailable");
    }

    private static bool ValidSnapshotLimit(int limit) => limit is >= 1 and <= CatalogFeedProtocol.MaximumPageSize;
    private static string SnapshotOwner(Guid library, Guid recipient) => $"{library:N}/{recipient:N}";
    private static string SnapshotPosition(string owner, int offset) => string.Create(CultureInfo.InvariantCulture, $"{owner}/{offset}");
}
