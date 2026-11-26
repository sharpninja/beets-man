using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Retains and resolves local listener drafts against an explicitly reviewed host revision.</summary>
/// <remarks>2026-11-25: Keep offline conflicts across replacement and restart for FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore
{
    /// <inheritdoc />
    Task<Outcome<ReplicaConflictReceipt>> ILibraryRecordOutboxStore.ResolveRecordConflictAsync(LibraryRecordScope scope, Guid operation,
        ResolveReplicaConflict request, CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var operations = PendingRecords();
        var key = ListenerKey(scope, operation);
        var serialized = JsonSerializer.Serialize(request);
        if (operations.FindById(key) is { } earlier)
        {
            if (!earlier["LibraryId"].IsGuid || earlier["LibraryId"].AsGuid != scope.LibraryId || earlier["ResolutionIntent"].AsString != serialized)
                return RecordConflict<ReplicaConflictReceipt>("operation-intent-mismatch");
            return SuccessRecord(new ReplicaConflictReceipt(request.ConflictId, request.Resolution, null));
        }
        var row = operations.FindById(ListenerKey(scope, request.ConflictId));
        if (row is null || !IsListenerOperation(row, scope) || row["State"].AsString != "Conflicted")
            return new Outcome<ReplicaConflictReceipt>.Invalid([new("library-record-conflict-unavailable")]);
        var current = ProjectListenerConflict(scope, row);
        if (current.HostRevision != request.ExpectedHostRevision)
            return RecordConflict<ReplicaConflictReceipt>("stale-replica-conflict", current.HostRevision);

        BsonDocument accepted;
        if (request.Resolution == ReplicaResolution.KeepHost)
        {
            accepted = new BsonDocument { ["_id"] = key, ["LibraryId"] = scope.LibraryId, ["OriginInstanceId"] = scope.OriginInstanceId,
                ["OperationId"] = operation, ["CommandType"] = nameof(ResolveReplicaConflict), ["State"] = "Accepted" };
        }
        else if (ReadRecordChange(row) is LibraryRecordIntent.Favorite favorite)
        {
            var intent = favorite with { ExpectedRevision = current.HostRevision };
            accepted = NewListenerOperation(scope, operation, current.RecordId, SerializeIntent(intent));
        }
        else
        {
            return new Outcome<ReplicaConflictReceipt>.Invalid([new("immutable-play-conflict")]);
        }
        accepted["ResolutionIntent"] = serialized;
        operations.Insert(accepted);
        row["State"] = "Resolved";
        row["ResolutionOperationId"] = operation;
        operations.Update(row);
        return SuccessRecord(new ReplicaConflictReceipt(request.ConflictId, request.Resolution, null));
    }, token), token);

    /// <summary>Reads the unresolved local drafts and their latest known host revision.</summary>
    private ImmutableArray<ReplicaConflict> ReadListenerConflicts(Guid library, Guid origin)
    {
        var scope = new LibraryRecordScope(library, origin);
        return [.. ListenerOperations(scope).Where(row => row["State"].AsString == "Conflicted").OrderBy(row => row["Order"].AsInt64)
            .Select(row => ProjectListenerConflict(scope, row))];
    }

    private ReplicaConflict ProjectListenerConflict(LibraryRecordScope scope, BsonDocument row)
    {
        var id = row["RecordId"].AsGuid;
        var intent = ReadRecordChange(row);
        var kind = intent is LibraryRecordIntent.Favorite ? CatalogEntityKind.Favorite : CatalogEntityKind.Play;
        var cached = CatalogProjectionTable(kind).FindById(id);
        var confirmedRevision = cached is not null && cached["LibraryId"].AsGuid == scope.LibraryId ? cached["SourceRevision"].AsInt64 : 0;
        var hostRevision = Math.Max(ReadRevision(row, "ActualHostRevision") ?? 0, confirmedRevision);
        var shared = kind == CatalogEntityKind.Favorite && hostRevision == confirmedRevision
            ? FavoriteLabel(cached is not null && !RecordFlag(cached, "Deleted")) : $"Host revision {hostRevision}";
        var local = intent is LibraryRecordIntent.Favorite favorite ? FavoriteLabel(favorite.IsFavorite) : "Local listening record";
        return new(row["OperationId"].AsGuid, id, hostRevision, shared, local);

        static string FavoriteLabel(bool active) => active ? "Favorite" : "Not a favorite";
    }
}
