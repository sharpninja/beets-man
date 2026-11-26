using System.Globalization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Stages the replacement separately from the live catalog until the full scope can commit atomically.</summary>
/// <remarks>2026-11-24: Recover expired replicas without rewriting local edits for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore
{
    private const string CatalogSnapshotStages = "catalog_snapshot_stages";
    private const string CatalogSnapshotStageRows = "catalog_snapshot_stage_rows";

    private static readonly CatalogEntityKind[] ReplacedCatalogKinds =
        [CatalogEntityKind.Artist, CatalogEntityKind.Album, CatalogEntityKind.Track, CatalogEntityKind.Playlist,
            CatalogEntityKind.Favorite, CatalogEntityKind.Play, CatalogEntityKind.SharedDefault];

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> ApplySnapshotPageAsync(CatalogSnapshotPage page, long expectedCursor, CancellationToken token) =>
        OnLaneAsync(() => Transact<ReplicationState>(() => ApplySnapshotPage(page, expectedCursor, token), token), token);

    private Outcome<ReplicationState> ApplySnapshotPage(CatalogSnapshotPage page, long expectedCursor, CancellationToken token)
    {
        if (!CatalogSnapshotProtocol.IsValid(page) || expectedCursor < 0 || page.UpperRevision < expectedCursor)
            return new Outcome<ReplicationState>.Invalid([new("invalid-catalog-snapshot-page")]);
        var library = page.LibraryId;
        var instance = page.RecipientInstanceId;
        var checkpoint = CatalogCheckpoint(library);
        var read = ReadReplicaState(library, instance, checkpoint);
        if (read is not Outcome<ReplicationState>.Success { Value: var state })
            return read;
        if (state.RecoveryOperationId == page.SnapshotId)
            return read;
        if (state.Cursor != expectedCursor)
            return new Outcome<ReplicationState>.Conflict("stale-replica-cursor", state.Cursor);

        var owner = $"{library:N}/{instance:N}";
        var stages = database.GetCollection<BsonDocument>(CatalogSnapshotStages);
        var rows = database.GetCollection<BsonDocument>(CatalogSnapshotStageRows);
        var stage = stages.FindById(owner);
        if (stage is null || stage["SnapshotId"].AsGuid != page.SnapshotId)
        {
            if (page.Offset != 0)
                return new Outcome<ReplicationState>.Conflict("catalog-snapshot-page-missing", state.Cursor);
            rows.DeleteMany(Query.EQ("Owner", owner));
            stage = new BsonDocument { ["_id"] = owner, ["SnapshotId"] = page.SnapshotId, ["UpperRevision"] = page.UpperRevision,
                ["ExpectedCursor"] = expectedCursor, ["TotalCount"] = page.TotalCount, ["ReceivedCount"] = 0 };
        }
        var received = stage["ReceivedCount"].AsInt32;
        if (stage["UpperRevision"].AsInt64 != page.UpperRevision || stage["ExpectedCursor"].AsInt64 != expectedCursor ||
            stage["TotalCount"].AsInt32 != page.TotalCount || page.Offset > received)
            return new Outcome<ReplicationState>.Conflict("catalog-snapshot-page-mismatch", state.Cursor);

        for (var index = 0; index < page.Records.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var position = page.Offset + index;
            var key = SnapshotStagePosition(owner, position);
            var serialized = JsonSerializer.Serialize(page.Records[index]);
            if (position < received)
            {
                if (rows.FindById(key) is not { } prior || prior["Payload"].AsString != serialized)
                    return new Outcome<ReplicationState>.Conflict("catalog-snapshot-page-mismatch", state.Cursor);
                continue;
            }
            rows.Insert(new BsonDocument { ["_id"] = key, ["Owner"] = owner, ["Payload"] = serialized });
        }
        received = Math.Max(received, page.NextOffset);
        if (received < page.TotalCount)
        {
            stage["ReceivedCount"] = received;
            stages.Upsert(stage);
            return read;
        }

        foreach (var projection in ReplacedCatalogKinds.Select(CatalogProjectionTable).DistinctBy(table => table.Name))
            projection.DeleteMany(Query.EQ("LibraryId", library));
        var identities = new HashSet<(CatalogEntityKind, Guid)>();
        for (var index = 0; index < received; index++)
        {
            token.ThrowIfCancellationRequested();
            var row = rows.FindById(SnapshotStagePosition(owner, index));
            if (row is null || JsonSerializer.Deserialize<CatalogSnapshotRecord>(row["Payload"].AsString) is not { } record)
                return new Outcome<ReplicationState>.DependencyFailure("catalog-snapshot-stage-incomplete", true);
            if (!identities.Add((record.Change.Kind, record.Change.RecordId)) || !ApplyCatalogChange(library, record.SourceRevision, record.Change))
                return new Outcome<ReplicationState>.Conflict("catalog-snapshot-record-conflict", state.Cursor);
        }
        stages.Delete(owner);
        rows.DeleteMany(Query.EQ("Owner", owner));
        return SaveReplicaCheckpoint(library, instance, page.UpperRevision, snapshotRequired: false, checkpoint, page.SnapshotId);
    }

    private static string SnapshotStagePosition(string owner, int offset) => string.Create(CultureInfo.InvariantCulture, $"{owner}/{offset}");
}
