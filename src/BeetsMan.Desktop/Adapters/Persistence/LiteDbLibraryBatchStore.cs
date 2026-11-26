using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Persistence;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Persists unpublished batch state on the same desktop database lane as other local records.</summary>
/// <remarks>2026-11-24: Map the batch entities for TR-LIBRARY-ARCH-002 and FR-LIBRARY-002#ac-batch-local, ac-batch-review, and ac-batch-revert.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IWriteUnitOfWork<ILibraryBatchRepository>, IQueuePlaylistBatchStore
{
    private const string BatchModes = "library_batch_modes";
    private const string BatchEntries = "library_batch_entries";
    private const string BatchReceipts = "library_batch_receipts";
    private const string BatchImportCopies = "library_batch_import_copies";

    /// <summary>Reads the local batch effects on the catalog's existing store lane.</summary>
    /// <remarks>2026-11-24: Read the working catalog and its local revision atomically for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
    internal LocalMaintenanceCatalog ReadLocalMaintenanceCatalog(LibraryBatchScope scope) => WithBatchRepository(repository =>
    {
        var mode = repository.Read(scope);
        var entries = mode.BatchId is { } batchId ? repository.ReadEntries(scope, batchId) : [];
        if (entries.Length != mode.ChangeCount)
            throw new InvalidDataException("The local batch does not contain all entries for its catalog projection.");
        return new LocalMaintenanceCatalog(scope.OriginInstanceId, mode.Revision, entries);
    });

    /// <inheritdoc />
    Task<Outcome<TResult>> IWriteUnitOfWork<ILibraryBatchRepository>.ExecuteAsync<TResult>(Func<ILibraryBatchRepository, Outcome<TResult>> write,
        CancellationToken token) => OnLaneAsync(() => Transact(() => WithBatchRepository(write), token), token);

    /// <inheritdoc />
    Task<Outcome<LibraryBatchStageReceipt>> IQueuePlaylistBatchStore.StageQueueBatchSaveAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
        Func<ILibraryBatchRepository, Outcome<LibraryBatchStageReceipt>> stage, CancellationToken token) => OnLaneAsync(() => Transact(() =>
        {
            if (checkpoint.PlaylistBacking is not { } binding)
                return new Outcome<LibraryBatchStageReceipt>.Invalid([new("queue-has-no-playlist")]);
            var result = WithBatchRepository(stage);
            if (result is not Outcome<LibraryBatchStageReceipt>.Success { Value: var receipt })
                return result;
            var saved = SaveCheckpoint(checkpoint with { PlaylistBacking = binding with { StagedSave = receipt } }, expectedRevision);
            return saved switch
            {
                Outcome<PlaybackCheckpoint>.Success => result,
                Outcome<PlaybackCheckpoint>.Invalid invalid => new Outcome<LibraryBatchStageReceipt>.Invalid(invalid.Issues),
                Outcome<PlaybackCheckpoint>.Conflict conflict => new Outcome<LibraryBatchStageReceipt>.Conflict(conflict.Code, conflict.ActualRevision),
                _ => throw new InvalidOperationException("Unexpected queue checkpoint result.")
            };
        }, token), token);

    private TResult WithBatchRepository<TResult>(Func<ILibraryBatchRepository, TResult> read)
    {
        var repository = new BatchRepository(database);
        try { return read(repository); }
        finally { repository.Close(); }
    }

    private sealed class BatchRepository(ILiteDatabase database) : ILibraryBatchRepository
    {
        private readonly int activeThread = Environment.CurrentManagedThreadId;
        private bool closed;

        public LibraryBatchRecord Read(LibraryBatchScope scope) => Find<LibraryBatchRecord>(BatchModes, ScopeKey(scope)) ?? new(null, 0, 0);

        public void Save(LibraryBatchScope scope, LibraryBatchRecord record) => Collection(BatchModes)
            .Upsert(new BsonDocument { ["_id"] = ScopeKey(scope), ["LibraryId"] = scope.LibraryId, ["OriginInstanceId"] = scope.OriginInstanceId,
                ["Payload"] = JsonSerializer.Serialize(record) });

        public ImmutableArray<LibraryBatchEntry> ReadEntries(LibraryBatchScope scope, Guid batchId) =>
            [.. Collection(BatchEntries).Find(Query.EQ("BatchKey", ScopedKey(scope, batchId))).OrderBy(row => row["Position"].AsInt32)
                .Select(Deserialize<LibraryBatchEntry>)];

        public LibraryBatchStageRecord? FindStage(LibraryBatchScope scope, Guid operationId) =>
            Find<LibraryBatchStageRecord>(BatchReceipts, ScopedKey(scope, operationId), "Stage");

        public LibraryBatchCommitRecord? FindCommit(LibraryBatchScope scope, Guid operationId) =>
            Find<LibraryBatchCommitRecord>(BatchReceipts, ScopedKey(scope, operationId), "Commit");

        public LibraryBatchEntry? FindEntry(LibraryBatchScope scope, Guid batchId, int position) =>
            Find<LibraryBatchEntry>(BatchEntries, EntryKey(ScopedKey(scope, batchId), position));

        public bool IsDirect(LibraryBatchScope scope, Guid operationId) =>
            Collection(BatchReceipts).FindById(ScopedKey(scope, operationId)) is { } row && row["Kind"].AsString == "Direct";

        public void RecordDirect(LibraryBatchScope scope, Guid operationId) => WriteReceipt<object?>(scope, operationId, "Direct", null);

        public void AddStage(LibraryBatchScope scope, LibraryBatchStageRecord stage, LibraryBatchEntry entry)
        {
            var batchKey = ScopedKey(scope, stage.Receipt.Version.BatchId);
            Collection(BatchEntries).Insert(new BsonDocument
            {
                ["_id"] = EntryKey(batchKey, entry.Position), ["BatchKey"] = batchKey,
                ["OperationId"] = entry.OperationId, ["Position"] = entry.Position, ["Payload"] = JsonSerializer.Serialize(entry)
            });
            WriteReceipt(scope, entry.OperationId, "Stage", stage);
        }

        public void RecordCommit(LibraryBatchScope scope, LibraryBatchCommitRecord committed) =>
            WriteReceipt(scope, committed.Seal.OperationId, "Commit", committed);

        /// <summary>Retains the published version on the original scoped queue stage for FR-PLAYER-001#ac-queue-autosave.</summary>
        public void RecordPublishedQueueSave(LibraryBatchScope scope, Guid operationId, PublishedBatchQueueSave published)
        {
            var receipts = Collection(BatchReceipts);
            var row = receipts.FindById(ScopedKey(scope, operationId));
            if (row is null || row["Kind"].AsString != "Stage")
                throw new InvalidDataException("The queue publication has no staged operation receipt.");
            var stage = Deserialize<LibraryBatchStageRecord>(row);
            row["Payload"] = JsonSerializer.Serialize(stage with { QueuePublication = published });
            receipts.Update(row);
        }

        public void RemoveEntries(LibraryBatchScope scope, Guid batchId) =>
            Collection(BatchEntries).DeleteMany(Query.EQ("BatchKey", ScopedKey(scope, batchId)));

        public void ClaimImportCopies(LibraryBatchScope scope, Guid operationId) => Collection(BatchImportCopies)
            .Upsert(new BsonDocument { ["_id"] = ScopedKey(scope, operationId), ["ScopeKey"] = ScopeKey(scope), ["OperationId"] = operationId });

        public ImmutableArray<Guid> ImportCopyClaims(LibraryBatchScope scope) =>
            [.. Collection(BatchImportCopies).Find(Query.EQ("ScopeKey", ScopeKey(scope))).Select(row => row["OperationId"].AsGuid)];

        public void ReleaseImportCopies(LibraryBatchScope scope, Guid operationId) => Collection(BatchImportCopies)
            .Delete(ScopedKey(scope, operationId));

        /// <summary>Rebinds the local draft without overwriting a subsequent edit for FR-LIBRARY-002#ac-batch-commit.</summary>
        public void BindPublishedDraft(LibraryBatchChange.Playlist staged, PlaylistVersion published)
        {
            var drafts = Collection("playlist_drafts");
            var repository = new Drafts(database);
            if (drafts.FindById(staged.DraftId) is null || repository.Find(staged.DraftId) is not { } current || current.BasePlaylist != staged.BasePlaylist)
                return;
            repository.TryReplace(current with { Revision = checked(current.Revision + 1), BasePlaylist = published,
                Name = current.Revision == staged.DraftRevision ? staged.Name : current.Name }, current.Revision);
        }

        public void Close() => closed = true;

        private void WriteReceipt<T>(LibraryBatchScope scope, Guid operationId, string kind, T receipt) =>
            Collection(BatchReceipts).Insert(new BsonDocument { ["_id"] = ScopedKey(scope, operationId), ["Kind"] = kind,
                ["LibraryId"] = scope.LibraryId, ["OriginInstanceId"] = scope.OriginInstanceId, ["OperationId"] = operationId,
                ["Payload"] = JsonSerializer.Serialize(receipt) });

        private T? Find<T>(string collection, string key, string? kind = null) where T : class =>
            Collection(collection).FindById(key) is { } row && (kind is null || row["Kind"].AsString == kind) ? Deserialize<T>(row) : null;

        private ILiteCollection<BsonDocument> Collection(string collection)
        {
            if (closed || Environment.CurrentManagedThreadId != activeThread)
                throw new InvalidOperationException("The batch repository is scoped to the active desktop write transaction.");
            return database.GetCollection<BsonDocument>(collection);
        }

        private static T Deserialize<T>(BsonDocument row) => JsonSerializer.Deserialize<T>(row["Payload"].AsString) ??
            throw new InvalidDataException("The desktop batch record is incomplete.");

        private static string ScopeKey(LibraryBatchScope scope) => $"{scope.LibraryId:N}/{scope.OriginInstanceId:N}";

        private static string ScopedKey(LibraryBatchScope scope, Guid id) => $"{ScopeKey(scope)}/{id:N}";

        private static string EntryKey(string key, int position) => string.Create(CultureInfo.InvariantCulture, $"{key}/{position}");
    }
}
