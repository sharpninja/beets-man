using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Persistence;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Owns one desktop LiteDB file and the atomic draft and pending-operation repositories.</summary>
/// <remarks>2026-11-24: Bind TR-RECORDS-ARCH-001#ac-litedb-repositories and ac-litedb-c4-entities to the desktop collection names.</remarks>
/// <remarks>2026-11-26: Validate the StoreSchema identity defined by TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IPlaylistDraftReader, IWriteUnitOfWork<IPlaylistDraftWriteRepositories>, IDisposable
{
    private readonly LiteDatabase database;
    private readonly SemaphoreSlim lane = new(1, 1);
    private bool disposed;

    private LiteDbDesktopPlaylistStore(LiteDatabase database, Guid storeId)
    {
        this.database = database;
        StoreId = storeId;
    }

    /// <summary>Gets the stable identity recorded in the desktop schema document.</summary>
    public Guid StoreId { get; }

    /// <summary>Opens an already validated absolute local path, optionally creating the new desktop store.</summary>
    /// <remarks>2026-11-26: Reject malformed or unsupported schema identities for FR-CONFIG-001#ac-repair before opening repositories.</remarks>
    public static LiteDbDesktopPlaylistStore Open(string databasePath, bool allowCreate)
    {
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("The desktop database path must be absolute.", nameof(databasePath));
        var path = Path.GetFullPath(databasePath);
        var exists = File.Exists(path);
        if (!exists && !allowCreate)
            throw new FileNotFoundException("The desktop database does not exist.", path);
        if (!exists)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var database = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        try
        {
            var schemas = database.GetCollection<BsonDocument>("store_schema");
            var schema = schemas.FindById("store");
            if (schema is null)
            {
                if (exists || database.GetCollectionNames().Any())
                    throw new InvalidDataException("The existing database does not contain a Beets Man store schema.");
                schema = new BsonDocument { ["_id"] = "store", ["StoreId"] = Guid.NewGuid(), ["StoreRole"] = "Desktop", ["SchemaVersion"] = 1 };
                schemas.Insert(schema);
            }
            if (!schema["StoreRole"].IsString || schema["StoreRole"].AsString != "Desktop" ||
                !schema["SchemaVersion"].IsInt32 || schema["SchemaVersion"].AsInt32 != 1 ||
                !schema["StoreId"].IsGuid || schema["StoreId"].AsGuid == Guid.Empty)
                throw new InvalidDataException("The database role or schema version is not supported by this desktop.");

            LiteDbCatalogReader.EnsureIndexes(database);
            database.GetCollection<BsonDocument>("pending_operations").EnsureIndex("QueueSessionId");
            // 2026-11-25: Scan pending rows for FR-RECORDS-001#ac-offline without reading retained receipts.
            database.GetCollection<BsonDocument>("pending_operations").EnsureIndex("State");
            // 2026-11-25: Read the local listener outbox by its owner and record for FR-RECORDS-001#ac-offline.
            database.GetCollection<BsonDocument>("pending_operations").EnsureIndex("ListenerScope");
            database.GetCollection<BsonDocument>("pending_operations").EnsureIndex("ListenerRecordKey");
            database.GetCollection<BsonDocument>("listening_records").EnsureIndex("LibraryId");
            // 2026-11-24: Support complete ordered batch review for FR-LIBRARY-002#ac-batch-review.
            database.GetCollection<BsonDocument>(BatchEntries).EnsureIndex("BatchKey");
            // 2026-11-24: Locate orphaned private import claims by scope for FR-LIBRARY-002#ac-batch-revert.
            database.GetCollection<BsonDocument>(BatchImportCopies).EnsureIndex("ScopeKey");
            // 2026-11-24: Discard private recovery rows by owner for FR-RECORDS-001#ac-replicate.
            database.GetCollection<BsonDocument>(CatalogSnapshotStageRows).EnsureIndex("Owner");
            return new LiteDbDesktopPlaylistStore(database, schema["StoreId"].AsGuid);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<Outcome<PlaylistDraft?>> ReadDraftAsync(Guid draftId, CancellationToken cancellationToken) =>
        OnLaneAsync<PlaylistDraft?>(() => new Outcome<PlaylistDraft?>.Success(new Drafts(database).Find(draftId)), cancellationToken);

    /// <summary>Lists pending named saves for one origin without reading queue or listener payloads.</summary>
    /// <remarks>2026-11-25: Recover existing rows through the repository for FR-RECORDS-001#ac-offline.</remarks>
    public Task<Outcome<ImmutableArray<PendingPlaylistSave>>> ReadPendingDraftSavesAsync(Guid originInstanceId, CancellationToken cancellationToken) =>
        ReadAsync<ImmutableArray<PendingPlaylistSave>>(db => new Outcome<ImmutableArray<PendingPlaylistSave>>.Success(
            [.. db.GetCollection<BsonDocument>("pending_operations").Find(Query.EQ("State", nameof(PendingPlaylistSaveStatus.Pending)))
                .Where(row => row["OriginInstanceId"].IsGuid && row["OriginInstanceId"].AsGuid == originInstanceId &&
                    row["CommandType"].AsString == nameof(SavePlaylist) && row["DraftId"].IsGuid && row["QueueSessionId"].IsNull)
                .Select(ReadOperation).OrderBy(save => save.DraftId).ThenBy(save => save.DraftRevision).ThenBy(save => save.Key.OperationId)]),
            cancellationToken);

    /// <summary>Runs a projection read on the same store lane as writes.</summary>
    /// <remarks>2026-11-24: Read one consistent catalog revision for FR-LIBRARY-001#ac-browse and ac-offline.</remarks>
    internal Task<Outcome<TResult>> ReadAsync<TResult>(Func<ILiteDatabase, Outcome<TResult>> read, CancellationToken token) =>
        OnLaneAsync(() => read(database), token);

    /// <inheritdoc />
    public Task<Outcome<TResult>> ExecuteAsync<TResult>(Func<IPlaylistDraftWriteRepositories, Outcome<TResult>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return OnLaneAsync(() => Transact(write, cancellationToken), cancellationToken);
    }

    /// <summary>Closes the store after its current operation ends.</summary>
    public void Dispose()
    {
        lane.Wait();
        try
        {
            if (!disposed)
            {
                disposed = true;
                database.Dispose();
            }
        }
        finally { lane.Release(); }
    }

    private async Task<Outcome<TResult>> OnLaneAsync<TResult>(Func<Outcome<TResult>> work, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return await Task.Run(work, token).ConfigureAwait(false);
            }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<TResult>.Cancelled();
        }
        catch (LiteException)
        {
            return new Outcome<TResult>.DependencyFailure("desktop-store-error", false);
        }
        catch (IOException)
        {
            return new Outcome<TResult>.DependencyFailure("desktop-store-unavailable", true);
        }
    }

    private Outcome<TResult> Transact<TResult>(Func<IPlaylistDraftWriteRepositories, Outcome<TResult>> write, CancellationToken token) =>
        Transact(() =>
        {
            var transaction = new RepositorySet(database);
            try { return write(transaction); }
            finally { transaction.Close(); }
        }, token);

    private Outcome<TResult> Transact<TResult>(Func<Outcome<TResult>> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!database.BeginTrans())
            throw new InvalidOperationException("The desktop write lane already has a transaction.");
        try
        {
            var result = write();
            if (result is not Outcome<TResult>.Success)
            {
                database.Rollback();
                return result;
            }
            token.ThrowIfCancellationRequested();
            if (!database.Commit())
                throw new InvalidOperationException("LiteDB did not commit the desktop write transaction.");
            return result;
        }
        catch
        {
            database.Rollback();
            throw;
        }
    }

    private sealed class RepositorySet : IPlaylistDraftWriteRepositories
    {
        private readonly Drafts drafts;
        private readonly Operations operations;
        private readonly int transactionThread = Environment.CurrentManagedThreadId;
        private bool active = true;

        public RepositorySet(ILiteDatabase database)
        {
            drafts = new(database, Check);
            operations = new(database, Check);
        }

        public IPlaylistDraftRepository Drafts => drafts;
        public IPendingPlaylistSaveRepository Operations => operations;
        public void Close() => active = false;

        private void Check()
        {
            if (!active || Environment.CurrentManagedThreadId != transactionThread)
                throw new InvalidOperationException("Repositories are scoped to the active transaction thread.");
        }
    }

    private sealed class Drafts(ILiteDatabase database, Action? check = null) : IPlaylistDraftRepository
    {
        private readonly ILiteCollection<BsonDocument> collection = database.GetCollection<BsonDocument>("playlist_drafts");

        public PlaylistDraft? Find(Guid draftId)
        {
            check?.Invoke();
            return collection.FindById(draftId) is { } row ? ReadDraft(row) : null;
        }

        public void Add(PlaylistDraft draft)
        {
            check?.Invoke();
            collection.Insert(WriteDraft(draft));
        }

        public bool TryReplace(PlaylistDraft draft, long expectedRevision)
        {
            if (Find(draft.DraftId)?.Revision != expectedRevision)
                return false;
            return collection.Update(WriteDraft(draft));
        }
    }

    private sealed class Operations(ILiteDatabase database, Action check) : IPendingPlaylistSaveRepository
    {
        private readonly ILiteCollection<BsonDocument> collection = database.GetCollection<BsonDocument>("pending_operations");

        public PendingPlaylistSave? Find(OperationKey key)
        {
            check();
            return collection.FindById(OperationId(key)) is { } row && row["CommandType"].AsString == nameof(SavePlaylist) ? ReadOperation(row) : null;
        }

        public void Add(PendingPlaylistSave operation)
        {
            check();
            collection.Insert(WriteOperation(operation));
        }

        public bool TryReplace(PendingPlaylistSave operation, PendingPlaylistSaveStatus expected)
        {
            if (Find(operation.Key)?.Status != expected)
                return false;
            return collection.Update(WriteOperation(operation));
        }
    }

    private static BsonDocument WriteDraft(PlaylistDraft draft) => new()
    {
        ["_id"] = draft.DraftId,
        ["PlaylistId"] = draft.BasePlaylist is { } saved ? saved.PlaylistId : BsonValue.Null,
        ["ExpectedHostRevision"] = draft.BasePlaylist is { } version ? version.Revision : BsonValue.Null,
        ["DraftRevision"] = draft.Revision, ["ProposedName"] = draft.Name, ["Entries"] = WriteEntries(draft.Entries)
    };

    private static PlaylistDraft ReadDraft(BsonDocument row) => new(row["_id"].AsGuid, row["DraftRevision"].AsInt64,
        row["ProposedName"].AsString, ReadEntries(row["Entries"].AsArray), ReadBacking(row, "PlaylistId", "ExpectedHostRevision"));

    private static string OperationId(OperationKey key) => $"{key.OriginInstanceId:N}/{key.OperationId:N}";

    private static BsonDocument WriteOperation(PendingPlaylistSave operation) => new()
    {
        ["_id"] = OperationId(operation.Key), ["OriginInstanceId"] = operation.Key.OriginInstanceId, ["OperationId"] = operation.Key.OperationId,
        ["CommandType"] = nameof(SavePlaylist), ["State"] = operation.Status.ToString(),
        ["DraftId"] = operation.DraftId != Guid.Empty ? operation.DraftId : BsonValue.Null,
        ["QueueSessionId"] = operation.QueueSource is { } origin ? origin.SessionId : BsonValue.Null,
        ["ExpectedRevision"] = operation.BasePlaylist is { } version ? version.Revision : BsonValue.Null,
        ["Payload"] = new BsonDocument
        {
            ["Version"] = 1, ["DraftRevision"] = operation.DraftRevision,
            ["PlaylistId"] = operation.BasePlaylist is { } saved ? saved.PlaylistId : BsonValue.Null,
            ["Name"] = operation.Name, ["Entries"] = WriteEntries(operation.Entries),
            ["CommittedPlaylistId"] = operation.Receipt is { } receipt ? receipt.PlaylistId : BsonValue.Null,
            ["CommittedRevision"] = operation.Receipt is { } committed ? committed.Revision : BsonValue.Null,
            ["CommittedDraftRevision"] = operation.Receipt?.DraftRevision is { } draftRevision ? draftRevision : BsonValue.Null,
            ["QueueSource"] = operation.QueueSource is { } source ? new BsonDocument
            {
                ["QueueRevision"] = source.QueueRevision, ["IntentHash"] = source.IntentHash,
                ["Predecessor"] = WriteOperationKey(source.Predecessor), ["ExpectedRevisionPinned"] = source.ExpectedRevisionPinned
            } : BsonValue.Null,
            ["ConflictCode"] = operation.ConflictCode is { } conflict ? conflict : BsonValue.Null,
            ["ActualHostRevision"] = operation.ActualHostRevision is { } actual ? actual : BsonValue.Null,
            ["Issues"] = new BsonArray(operation.Issues.IsDefault ? [] : operation.Issues.Select(issue =>
                (BsonValue)new BsonDocument { ["Code"] = issue.Code, ["Field"] = issue.Field is { } field ? field : BsonValue.Null }))
        }
    };

    private static PendingPlaylistSave ReadOperation(BsonDocument row)
    {
        var payload = row["Payload"].AsDocument;
        if (row["CommandType"].AsString != nameof(SavePlaylist) || payload["Version"].AsInt32 != 1)
            throw new InvalidDataException("The pending playlist operation has an unsupported payload.");
        var draftId = row["DraftId"].IsGuid ? row["DraftId"].AsGuid : Guid.Empty;
        var source = row["QueueSessionId"].IsGuid && payload["QueueSource"].IsDocument
            ? ReadQueueSource(row["QueueSessionId"].AsGuid, payload["QueueSource"].AsDocument) : null;
        var backing = payload["PlaylistId"].IsNull ? null : new PlaylistVersion(payload["PlaylistId"].AsGuid, row["ExpectedRevision"].AsInt64);
        var receipt = payload["CommittedPlaylistId"].IsNull ? null : new PlaylistSavedReceipt(draftId,
            payload["CommittedPlaylistId"].AsGuid, payload["CommittedRevision"].AsInt64, ReadRevision(payload, "CommittedDraftRevision"), source?.SessionId);
        var issues = payload["Issues"].IsArray ? payload["Issues"].AsArray.Select(issue =>
            new ValidationIssue(issue.AsDocument["Code"].AsString, ReadString(issue.AsDocument, "Field"))).ToImmutableArray() : [];
        return new(new(row["OriginInstanceId"].AsGuid, row["OperationId"].AsGuid), draftId, payload["DraftRevision"].AsInt64,
            backing, payload["Name"].AsString, ReadEntries(payload["Entries"].AsArray),
            Enum.Parse<PendingPlaylistSaveStatus>(row["State"].AsString), receipt, ReadString(payload, "ConflictCode"),
            ReadRevision(payload, "ActualHostRevision"), issues, source);
    }

    private static QueuePlaylistSaveSource ReadQueueSource(Guid sessionId, BsonDocument source) => new(sessionId, source["QueueRevision"].AsInt64,
        source["IntentHash"].AsString, ReadOperationKey(source["Predecessor"]), source["ExpectedRevisionPinned"].AsBoolean);

    private static BsonValue WriteOperationKey(OperationKey? key) => key is { } identity
        ? new BsonDocument { ["OriginInstanceId"] = identity.OriginInstanceId, ["OperationId"] = identity.OperationId }
        : BsonValue.Null;

    private static OperationKey? ReadOperationKey(BsonValue key) => key.IsDocument
        ? new(key.AsDocument["OriginInstanceId"].AsGuid, key.AsDocument["OperationId"].AsGuid) : null;

    private static PlaylistVersion? ReadBacking(BsonDocument row, string id, string revision) =>
        row[id].IsNull ? null : new(row[id].AsGuid, row[revision].AsInt64);

    private static long? ReadRevision(BsonDocument row, string name) => row[name].IsNull ? null : row[name].AsInt64;
    private static string? ReadString(BsonDocument row, string name) => row[name].IsNull ? null : row[name].AsString;

    private static BsonArray WriteEntries(ImmutableArray<PlaylistEntry> entries) => new(entries.Select(entry =>
        (BsonValue)new BsonDocument { ["EntryId"] = entry.EntryId, ["TrackId"] = entry.TrackId }));

    private static ImmutableArray<PlaylistEntry> ReadEntries(BsonArray entries) =>
        [.. entries.Select(entry => new PlaylistEntry(entry.AsDocument["EntryId"].AsGuid, entry.AsDocument["TrackId"].AsGuid))];
}
