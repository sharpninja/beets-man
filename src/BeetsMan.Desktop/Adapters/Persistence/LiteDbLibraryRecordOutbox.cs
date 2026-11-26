using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Persists offline favorite and play intents independently of the authoritative replica.</summary>
/// <remarks>2026-11-25: Keep local intents, predecessor revisions and receipts for FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : ILibraryRecordOutboxStore
{
    private const string ListenerCommandType = "LibraryRecord";

    /// <inheritdoc />
    Task<Outcome<FavoriteState>> ILibraryRecordOutboxStore.ReadFavoriteAsync(LibraryRecordScope scope, Guid track, CancellationToken token) =>
        OnLaneAsync(() => SuccessRecord(ReadEffectiveFavorite(scope, track)), token);

    /// <inheritdoc />
    Task<Outcome<ImmutableArray<RecordedPlay>>> ILibraryRecordOutboxStore.ReadHistoryAsync(LibraryRecordScope scope, int limit, CancellationToken token) =>
        OnLaneAsync(() =>
        {
            var cached = CatalogProjectionTable(CatalogEntityKind.Play).Find(Query.EQ("LibraryId", scope.LibraryId))
                .Where(row => row["Kind"].AsString == nameof(CatalogEntityKind.Play) && !RecordFlag(row, "Deleted") && row["PlayedAtUtcTicks"].IsInt64)
                .Select(row => new RecordedPlay(row["_id"].AsGuid, row["TrackId"].AsGuid, new(row["PlayedAtUtcTicks"].AsInt64, TimeSpan.Zero), false));
            var pending = ListenerOperations(scope).Where(row => row["State"].AsString == "Pending").Select(ReadRecordChange)
                .OfType<LibraryRecordIntent.Play>().Select(play => new RecordedPlay(play.PlayId, play.TrackId, play.PlayedAtUtc, true));
            return SuccessRecord(cached.Concat(pending).DistinctBy(play => play.PlayId).OrderByDescending(play => play.PlayedAtUtc)
                .ThenBy(play => play.PlayId).Take(limit).ToImmutableArray());
        }, token);

    /// <inheritdoc />
    Task<Outcome<FavoriteState>> ILibraryRecordOutboxStore.StageFavoriteAsync(LibraryRecordScope scope, Guid operation, SetFavorite command,
        CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var intent = new LibraryRecordIntent.Favorite(command.TrackId, command.IsFavorite, command.ExpectedRevision);
        var payload = SerializeIntent(intent);
        if (PendingRecords().FindById(ListenerKey(scope, operation)) is { } prior)
            return MatchesIntent(prior, scope, payload) ? SuccessRecord(ReadEffectiveFavorite(scope, command.TrackId)) : RecordConflict<FavoriteState>("operation-intent-mismatch");
        var current = ReadEffectiveFavorite(scope, command.TrackId);
        if (current.Revision != command.ExpectedRevision)
            return RecordConflict<FavoriteState>("stale-local-favorite", current.Revision);
        var id = LibraryRecordIdentity.Favorite(scope.LibraryId, command.TrackId);
        var previous = PendingFavoriteOperations(scope, id)
            .OrderByDescending(row => row["Order"].AsInt64).FirstOrDefault();
        var next = NewListenerOperation(scope, operation, id, payload);
        if (previous is not null)
            next["Predecessor"] = previous["OperationId"];
        PendingRecords().Insert(next);
        return SuccessRecord(current with { IsFavorite = command.IsFavorite, PendingSync = true });
    }, token), token);

    /// <inheritdoc />
    Task<Outcome<RecordedPlay>> ILibraryRecordOutboxStore.StagePlayAsync(LibraryRecordScope scope, Guid operation, RecordPlay command,
        CancellationToken token) => OnLaneAsync(() => Transact(() => StageListenerPlay(scope, operation, command), token), token);

    /// <summary>Stages a play from either a command or a playback checkpoint transaction.</summary>
    /// <remarks>2026-11-25: Share operation identity validation for FR-RECORDS-001#ac-offline.</remarks>
    private Outcome<RecordedPlay> StageListenerPlay(LibraryRecordScope scope, Guid operation, RecordPlay command)
    {
        var intent = new LibraryRecordIntent.Play(command.PlayId, command.TrackId, command.PlayedAtUtc);
        var payload = SerializeIntent(intent);
        var result = new RecordedPlay(command.PlayId, command.TrackId, command.PlayedAtUtc, true);
        if (PendingRecords().FindById(ListenerKey(scope, operation)) is { } prior)
            return MatchesIntent(prior, scope, payload) ? SuccessRecord(result with { PendingSync = prior["State"].AsString == "Pending" }) :
                RecordConflict<RecordedPlay>("operation-intent-mismatch");
        PendingRecords().Insert(NewListenerOperation(scope, operation, command.PlayId, payload));
        return SuccessRecord(result);
    }

    /// <inheritdoc />
    Task<Outcome<ImmutableArray<Guid>>> ILibraryRecordOutboxStore.ReadPendingRecordOperationsAsync(LibraryRecordScope scope, CancellationToken token) =>
        OnLaneAsync(() => SuccessRecord(ListenerOperations(scope).Where(row => row["State"].AsString == "Pending")
            .OrderBy(row => row["Order"].AsInt64).Select(row => row["OperationId"].AsGuid).ToImmutableArray()), token);

    /// <inheritdoc />
    Task<Outcome<LibraryRecordAttempt?>> ILibraryRecordOutboxStore.PrepareRecordAsync(LibraryRecordScope scope, Guid operation, CancellationToken token) =>
        OnLaneAsync(() => Transact(() =>
        {
            var operations = PendingRecords();
            var row = PendingListenerOperation(scope, operation);
            if (row is null)
                return SuccessRecord<LibraryRecordAttempt?>(null);
            var intent = ReadRecordChange(row);
            if (row["Predecessor"].IsGuid && !RecordFlag(row, "RevisionPinned"))
            {
                var previous = operations.FindById(ListenerKey(scope, row["Predecessor"].AsGuid));
                if (previous is not null && IsListenerOperation(previous, scope) && previous["RecordId"] == row["RecordId"])
                {
                    if (previous["State"].AsString == "Pending")
                        return SuccessRecord<LibraryRecordAttempt?>(null);
                    if (intent is LibraryRecordIntent.Favorite favorite && previous["State"].AsString == "Accepted" && previous["ReceiptRevision"].IsNumber)
                    {
                        intent = favorite with { ExpectedRevision = previous["ReceiptRevision"].AsInt64 };
                        row["Payload"] = SerializeIntent(intent);
                        row["RevisionPinned"] = true;
                        operations.Update(row);
                        return SuccessRecord<LibraryRecordAttempt?>(new(operation, row["RecordId"].AsGuid, intent));
                    }
                }
                SaveListenerConflict(row, "library-record-predecessor-unresolved", previous is null ? null : ReadRevision(previous, "ActualHostRevision"));
                return SuccessRecord<LibraryRecordAttempt?>(null);
            }
            return SuccessRecord<LibraryRecordAttempt?>(new(operation, row["RecordId"].AsGuid, intent));
        }, token), token);

    /// <inheritdoc />
    Task<Outcome<bool>> ILibraryRecordOutboxStore.AcceptRecordAsync(LibraryRecordScope scope, LibraryRecordAttempt attempt, LibraryRecordReceipt receipt,
        CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var matches = receipt.LibraryId == scope.LibraryId && receipt.OriginInstanceId == scope.OriginInstanceId &&
            receipt.OperationId == attempt.OperationId && receipt.RecordId == attempt.RecordId && receipt.RecordRevision >= 0 &&
            receipt.FeedRevision >= receipt.RecordRevision;
        var row = PendingListenerOperation(scope, attempt.OperationId);
        if (!matches || row is null || !MatchesAttempt(row, attempt))
            return RecordConflict<bool>("library-record-receipt-mismatch");
        var projection = attempt.Change switch
        {
            LibraryRecordIntent.Favorite favorite when favorite.IsFavorite => new CatalogFeedChange(CatalogEntityKind.Favorite, CatalogEntityOperation.Upsert,
                attempt.RecordId, new ReplicaFavorite(favorite.TrackId)),
            LibraryRecordIntent.Favorite => new CatalogFeedChange(CatalogEntityKind.Favorite, CatalogEntityOperation.Delete, attempt.RecordId),
            LibraryRecordIntent.Play play => new CatalogFeedChange(CatalogEntityKind.Play, CatalogEntityOperation.Upsert, attempt.RecordId,
                new ReplicaPlay(play.TrackId, play.PlayedAtUtc)),
            _ => throw new InvalidDataException("The local listener operation is not supported.")
        };
        if (receipt.RecordRevision == 0 ? projection.Operation != CatalogEntityOperation.Delete :
            !ApplyCatalogChange(scope.LibraryId, receipt.RecordRevision, projection))
            return RecordConflict<bool>("library-record-projection-mismatch");
        row["ReceiptRevision"] = receipt.RecordRevision;
        row["State"] = "Accepted";
        PendingRecords().Update(row);
        return SuccessRecord(true);
    }, token), token);

    /// <inheritdoc />
    Task<Outcome<bool>> ILibraryRecordOutboxStore.KeepRecordConflictAsync(LibraryRecordScope scope, LibraryRecordAttempt attempt,
        string code, long? actualRevision, CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        if (PendingListenerOperation(scope, attempt.OperationId) is not { } row || !MatchesAttempt(row, attempt))
            return RecordConflict<bool>("library-record-attempt-changed");
        SaveListenerConflict(row, code, actualRevision);
        return SuccessRecord(true);
    }, token), token);

    /// <summary>Reads the latest authored value while a favorite is pending and the shared version once its result settles.</summary>
    private FavoriteState ReadEffectiveFavorite(LibraryRecordScope scope, Guid track)
    {
        var id = LibraryRecordIdentity.Favorite(scope.LibraryId, track);
        var cached = CatalogProjectionTable(CatalogEntityKind.Favorite).FindById(id);
        var local = PendingFavoriteOperations(scope, id)
            .OrderByDescending(row => row["Order"].AsInt64).FirstOrDefault();
        var changed = local is null ? null : ReadRecordChange(local) as LibraryRecordIntent.Favorite;
        return new(track, changed?.IsFavorite ?? (cached is not null && !RecordFlag(cached, "Deleted")), cached?["SourceRevision"].AsInt64 ?? 0, changed is not null);
    }

    private BsonDocument NewListenerOperation(LibraryRecordScope scope, Guid operation, Guid id, string payload) => new()
    {
        ["_id"] = ListenerKey(scope, operation), ["LibraryId"] = scope.LibraryId, ["OriginInstanceId"] = scope.OriginInstanceId,
        ["ListenerScope"] = ListenerScope(scope), ["ListenerRecordKey"] = ListenerRecordKey(scope, id),
        ["OperationId"] = operation, ["RecordId"] = id, ["CommandType"] = ListenerCommandType, ["State"] = "Pending",
        ["Payload"] = payload, ["OriginalPayload"] = payload,
        ["Order"] = NextListenerOrder(scope)
    };

    /// <summary>Allocates stable operation order for this desktop and library in the staging transaction.</summary>
    private long NextListenerOrder(LibraryRecordScope scope)
    {
        var counters = database.GetCollection<BsonDocument>("sync_checkpoints");
        var key = $"{ListenerScope(scope)}/listener-outbox";
        var counter = counters.FindById(key) ?? new BsonDocument { ["_id"] = key, ["LibraryId"] = scope.LibraryId,
            ["OriginInstanceId"] = scope.OriginInstanceId, ["Feed"] = "listener-outbox", ["LastOrder"] = 0L };
        var order = checked(counter["LastOrder"].AsInt64 + 1);
        counter["LastOrder"] = order;
        counters.Upsert(counter);
        return order;
    }

    private void SaveListenerConflict(BsonDocument row, string code, long? actual)
    {
        row["State"] = "Conflicted";
        row["ConflictCode"] = code;
        row["ActualHostRevision"] = actual is long revision ? new BsonValue(revision) : BsonValue.Null;
        PendingRecords().Update(row);
    }

    private ILiteCollection<BsonDocument> PendingRecords() => database.GetCollection<BsonDocument>("pending_operations");
    private IEnumerable<BsonDocument> ListenerOperations(LibraryRecordScope scope) => PendingRecords().Find(Query.EQ("ListenerScope", ListenerScope(scope)));
    private IEnumerable<BsonDocument> PendingFavoriteOperations(LibraryRecordScope scope, Guid id) => PendingRecords().Find(
        Query.And(Query.EQ("ListenerRecordKey", ListenerRecordKey(scope, id)), Query.EQ("State", "Pending")));

    private BsonDocument? PendingListenerOperation(LibraryRecordScope scope, Guid operation) =>
        PendingRecords().FindById(ListenerKey(scope, operation)) is { } row && IsListenerOperation(row, scope) && row["State"].AsString == "Pending" ? row : null;

    private static bool IsListenerOperation(BsonDocument row, LibraryRecordScope scope) => row["LibraryId"].IsGuid && row["LibraryId"].AsGuid == scope.LibraryId &&
        row["OriginInstanceId"].IsGuid && row["OriginInstanceId"].AsGuid == scope.OriginInstanceId && row["CommandType"].AsString == ListenerCommandType;

    private static bool MatchesIntent(BsonDocument row, LibraryRecordScope scope, string payload) => IsListenerOperation(row, scope) && row["OriginalPayload"].AsString == payload;
    private static bool MatchesAttempt(BsonDocument row, LibraryRecordAttempt attempt) => row["RecordId"].AsGuid == attempt.RecordId && ReadRecordChange(row) == attempt.Change;
    private static string ListenerKey(LibraryRecordScope scope, Guid operation) => OperationId(new(scope.OriginInstanceId, operation));
    private static string ListenerScope(LibraryRecordScope scope) => $"{scope.LibraryId:N}/{scope.OriginInstanceId:N}";
    private static string ListenerRecordKey(LibraryRecordScope scope, Guid record) => $"{ListenerScope(scope)}/{record:N}";
    private static string SerializeIntent(LibraryRecordIntent intent) => JsonSerializer.Serialize(intent);
    private static LibraryRecordIntent ReadRecordChange(BsonDocument row) => JsonSerializer.Deserialize<LibraryRecordIntent>(row["Payload"].AsString) ??
        throw new InvalidDataException("The desktop listener intent is unavailable.");
    private static bool RecordFlag(BsonDocument row, string key) => row[key].IsBoolean && row[key].AsBoolean;
    private static Outcome<T> SuccessRecord<T>(T value) => new Outcome<T>.Success(value);
    private static Outcome<T> RecordConflict<T>(string code, long? revision = null) => new Outcome<T>.Conflict(code, revision);
}
