using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Owns the host playlist and publication repositories on a single database lane.</summary>
/// <remarks>2026-11-24: Map the host LiteDB entities for TR-RECORDS-ARCH-001#ac-litedb-repositories and ac-litedb-c4-entities.</remarks>
/// <remarks>2026-11-25: Use the host's injected activity clock for FR-RECORDS-001#ac-register.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IHostPlaylistUnitOfWork, IHostDevicePlaylistStore, IHostDeviceExportSources, IHostDeviceSyncListStore,
    IHostExportPlaylistLookup,
    IHostDeviceExportQueryStore, IDeviceExportPublicationJournal, IDeviceExportPreviewStore, IDeviceExportScanStore,
    IDeviceExportReceiverStore, IDeviceExportTransferStore, IDisposable
{
    private readonly LiteDatabase database;
    private readonly TimeProvider activityClock;
    private readonly SemaphoreSlim lane = new(1, 1);
    private bool disposed;

    private LiteDbHostPlaylistStore(LiteDatabase database, TimeProvider activityClock)
    {
        this.database = database;
        this.activityClock = activityClock;
    }

    /// <summary>Opens one validated host-local database and initializes a new host schema when allowed.</summary>
    public static LiteDbHostPlaylistStore Open(string databasePath, bool allowCreate, TimeProvider? activityClock = null)
    {
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("The host database path must be absolute.", nameof(databasePath));
        var path = Path.GetFullPath(databasePath);
        var existing = File.Exists(path);
        if (!existing && !allowCreate)
            throw new FileNotFoundException("The host database does not exist.", path);
        if (!existing)
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct });
        try
        {
            var schemas = database.GetCollection<BsonDocument>("store_schema");
            var schema = schemas.FindById("store");
            if (schema is null)
            {
                if (existing || database.GetCollectionNames().Any())
                    throw new InvalidDataException("The existing database does not contain a Beets Man store schema.");
                schema = new() { ["_id"] = "store", ["StoreId"] = Guid.NewGuid(), ["StoreRole"] = "Host", ["SchemaVersion"] = 1 };
                schemas.Insert(schema);
            }
            if (!schema["StoreRole"].IsString || schema["StoreRole"].AsString != "Host" ||
                !schema["SchemaVersion"].IsInt32 || schema["SchemaVersion"].AsInt32 != 1 ||
                !schema["StoreId"].IsGuid || schema["StoreId"].AsGuid == Guid.Empty)
                throw new InvalidDataException("The database role or schema version is not supported by the host.");
            database.GetCollection<BsonDocument>("registered_instances").EnsureIndex("LibraryId");
            database.GetCollection<BsonDocument>("notification_deliveries").EnsureIndex("RecipientInstanceId");
            database.GetCollection<BsonDocument>("notification_deliveries").EnsureIndex("LibraryId");
            // 2026-11-24: Discard a superseded capture by recipient for FR-RECORDS-001#ac-replicate.
            database.GetCollection<BsonDocument>(CatalogSnapshotRows).EnsureIndex("Owner");
            // 2026-11-24: Bind distinct approvals and ordered journal entries for FR-LIBRARY-002#ac-batch-commit.
            database.GetCollection<BsonDocument>(ApprovedBatchPublications).EnsureIndex("BatchKey", unique: true);
            database.GetCollection<BsonDocument>(ApprovedBatchEntries).EnsureIndex("PublicationKey");
            // 2026-11-24: Read the exact completed transfers for FR-LIBRARY-002#ac-batch-commit.
            database.GetCollection<BsonDocument>(ApprovedBatchImportParts).EnsureIndex("PublicationKey");
            EnsureDeviceExportIndexes(database);
            EnsurePublicationIndexes(database);
            EnsureExportScanIndexes(database);
            return new(database, activityClock ?? TimeProvider.System);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<HostOutcome<T>> ExecuteAsync<T>(Func<IHostPlaylistWriteRepositories, HostOutcome<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return OnLaneAsync(() => Execute(write, cancellationToken), cancellationToken);
    }

    /// <summary>Returns the authoritative playlist within the selected library.</summary>
    public Task<HostOutcome<SavedHostPlaylist?>> ReadPlaylistAsync(Guid libraryId, Guid playlistId, CancellationToken cancellationToken) =>
        OnLaneAsync<SavedHostPlaylist?>(() => new HostOutcome<SavedHostPlaylist?>.Success(
            MatchPlaylist(database.GetCollection<BsonDocument>("saved_playlists"), libraryId, playlistId)), cancellationToken);

    /// <summary>Returns the feed head, selected change, and its pending deliveries from one store view.</summary>
    public Task<HostOutcome<HostPlaylistPublication>> ReadPublicationAsync(Guid libraryId, long revision, CancellationToken cancellationToken) =>
        OnLaneAsync<HostPlaylistPublication>(() =>
        {
            var head = database.GetCollection<BsonDocument>("revision_heads").FindById(libraryId);
            var change = database.GetCollection<BsonDocument>("revision_changes").FindById(FeedId(libraryId, revision));
            var deliveries = database.GetCollection<BsonDocument>("notification_deliveries").Find(Query.EQ("LibraryId", libraryId))
                .Where(row => row["Revision"].AsInt64 == revision && row["Status"].AsString == "Pending")
                .Select(row => row["RecipientInstanceId"].AsGuid).Order().ToImmutableArray();
            var playlist = change?["Changes"].AsArray.FirstOrDefault(value => value.AsDocument["Kind"].AsString == "Playlist");
            var updated = playlist is null ? null : ReadPlaylist(playlist.AsDocument["Payload"].AsDocument);
            HostOperationKey? origin = change is null ? null : new(change["OriginInstanceId"].AsGuid, change["OperationId"].AsGuid);
            return new HostOutcome<HostPlaylistPublication>.Success(new(head?["CurrentRevision"].AsInt64 ?? 0, updated, origin, deliveries));
        }, cancellationToken);

    /// <inheritdoc />
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

    private HostOutcome<T> Execute<T>(Func<IHostPlaylistWriteRepositories, HostOutcome<T>> write, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!database.BeginTrans())
            throw new InvalidOperationException("A host write transaction is already open on this lane.");
        try
        {
            var repositories = new Repositories(database);
            HostOutcome<T> result;
            try { result = write(repositories); }
            finally { repositories.Close(); }
            if (result is not HostOutcome<T>.Success)
            {
                database.Rollback();
                return result;
            }
            token.ThrowIfCancellationRequested();
            if (!database.Commit())
                throw new InvalidOperationException("LiteDB did not commit the host transaction.");
            return result;
        }
        catch
        {
            database.Rollback();
            throw;
        }
    }

    private async Task<HostOutcome<T>> OnLaneAsync<T>(Func<HostOutcome<T>> work, CancellationToken token)
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
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<T>.Cancelled(); }
        catch (Exception exception) when (exception is LiteException or IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<T>.Unavailable("host-store-unavailable");
        }
    }

    private static SavedHostPlaylist? MatchPlaylist(ILiteCollection<BsonDocument> playlists, Guid libraryId, Guid id) =>
        playlists.FindById(id) is { } row && row["LibraryId"].AsGuid == libraryId ? ReadPlaylist(row) : null;

    private static SavedHostPlaylist ReadPlaylist(BsonDocument row) => new(row["LibraryId"].AsGuid, row["_id"].AsGuid, row["Revision"].AsInt64,
        row["Name"].AsString, [.. row["Entries"].AsArray.Select(item => new PlaylistEntryData(item.AsDocument["EntryId"].AsGuid, item.AsDocument["TrackId"].AsGuid))]);

    private static BsonDocument WritePlaylist(SavedHostPlaylist playlist) => new()
    {
        ["_id"] = playlist.PlaylistId, ["LibraryId"] = playlist.LibraryId, ["Revision"] = playlist.Revision, ["Name"] = playlist.Name,
        ["Entries"] = new BsonArray(playlist.Entries.Select(entry => (BsonValue)new BsonDocument
        {
            ["EntryId"] = entry.EntryId, ["TrackId"] = entry.TrackId
        }))
    };

    private static string FeedId(Guid libraryId, long revision) => $"{libraryId:N}/{revision.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Reads the mutable feed head inside the active host transaction.</summary>
    /// <remarks>2026-11-24: Share one revision feed across playlist and import publication for FR-RECORDS-001#ac-replicate.</remarks>
    private static BsonDocument ReadFeedHead(ILiteDatabase db, Guid libraryId) =>
        db.GetCollection<BsonDocument>("revision_heads").FindById(libraryId) ??
        new BsonDocument { ["_id"] = libraryId, ["CurrentRevision"] = 0L, ["MinRetainedRevision"] = 0L };

    private static BsonDocument FeedUpsert(string kind, BsonDocument payload) => new()
        { ["Version"] = 1, ["Kind"] = kind, ["Operation"] = "Upsert", ["Payload"] = payload };

    /// <summary>Appends a revision and the current active delivery recipients in the owning transaction.</summary>
    private static void AppendFeed(ILiteDatabase db, BsonDocument head, long revision, HostOperationKey origin, BsonArray updates)
    {
        var libraryId = head["_id"].AsGuid;
        head["CurrentRevision"] = revision;
        db.GetCollection<BsonDocument>("revision_heads").Upsert(head);
        var key = FeedId(libraryId, revision);
        db.GetCollection<BsonDocument>("revision_changes").Insert(new BsonDocument
        {
            ["_id"] = key, ["LibraryId"] = libraryId, ["Revision"] = revision, ["OriginInstanceId"] = origin.OriginInstanceId,
            ["OperationId"] = origin.OperationId, ["Changes"] = updates
        });
        var recipients = db.GetCollection<BsonDocument>("registered_instances").Find(Query.EQ("LibraryId", libraryId))
            .Where(row => Text(row, "Status") == "Active").Select(row => row["_id"].AsGuid)
            .Where(id => id != origin.OriginInstanceId).ToArray();
        var deliveries = db.GetCollection<BsonDocument>("notification_deliveries");
        foreach (var recipient in recipients)
        {
            deliveries.Insert(new BsonDocument
            {
                ["_id"] = $"{key}/{recipient:N}", ["LibraryId"] = libraryId, ["RecipientInstanceId"] = recipient,
                ["Revision"] = revision, ["Status"] = "Pending"
            });
        }
    }
    private static string OperationId(HostOperationKey key) => $"{key.OriginInstanceId:N}/{key.OperationId:N}";

    private static bool IsActiveInstance(ILiteCollection<BsonDocument> instances, Guid libraryId, Guid instanceId) =>
        instances.FindById(instanceId) is { } registered && registered["LibraryId"].IsGuid && registered["LibraryId"].AsGuid == libraryId &&
        registered["Status"].IsString && registered["Status"].AsString == "Active";

    private sealed class Repositories(ILiteDatabase database) : IHostPlaylistWriteRepositories, ISavedHostPlaylistRepository,
        IHostPlaylistOperationRepository, IHostPlaylistRegistry, IHostPlaylistPublicationRepository
    {
        private readonly int transactionThread = Environment.CurrentManagedThreadId;
        private readonly ILiteCollection<BsonDocument> playlists = database.GetCollection<BsonDocument>("saved_playlists");
        private readonly ILiteCollection<BsonDocument> operations = database.GetCollection<BsonDocument>("operation_receipts");
        private readonly ILiteCollection<BsonDocument> instances = database.GetCollection<BsonDocument>("registered_instances");
        private bool open = true;

        public ISavedHostPlaylistRepository Playlists => this;
        public IHostPlaylistOperationRepository Operations => this;
        public IHostPlaylistRegistry Registry => this;
        public IHostPlaylistPublicationRepository Publications => this;
        public void Close() => open = false;

        public SavedHostPlaylist? Find(Guid libraryId, Guid playlistId)
        {
            Check();
            return MatchPlaylist(playlists, libraryId, playlistId);
        }

        public void Add(SavedHostPlaylist playlist)
        {
            Check();
            playlists.Insert(WritePlaylist(playlist));
        }

        public bool TryReplace(SavedHostPlaylist playlist, long expectedRevision) =>
            Find(playlist.LibraryId, playlist.PlaylistId)?.Revision == expectedRevision && playlists.Update(WritePlaylist(playlist));

        public bool IsActive(Guid libraryId, Guid instanceId)
        {
            Check();
            return IsActiveInstance(instances, libraryId, instanceId);
        }

        public SavedHostPlaylistOperation? Find(HostOperationKey key)
        {
            Check();
            var row = operations.FindById(OperationId(key));
            if (row is null)
                return null;
            var stored = row["Receipt"].AsDocument;
            var receipt = stored["Type"].AsString == "PlaylistSaved" && stored["Version"].AsInt32 == 1
                ? new PlaylistSaveResponse(key.OperationId, new((PlaylistSaveSourceKind)stored["SourceKind"].AsInt32, stored["SourceId"].AsGuid),
                    stored["PlaylistId"].AsGuid, stored["PlaylistRevision"].AsInt64, stored["FeedRevision"].AsInt64)
                : null;
            return new(row["IntentFingerprint"].AsString, receipt);
        }

        public void Add(HostOperationKey key, SavedHostPlaylistOperation operation)
        {
            Check();
            var receipt = operation.Receipt ?? throw new ArgumentException("A committed playlist receipt is required.", nameof(operation));
            operations.Insert(new BsonDocument
            {
                ["_id"] = OperationId(key), ["OriginInstanceId"] = key.OriginInstanceId, ["OperationId"] = key.OperationId,
                ["IntentFingerprint"] = operation.IntentFingerprint, ["Receipt"] = new BsonDocument
                {
                    ["Type"] = "PlaylistSaved", ["Version"] = 1, ["SourceKind"] = (int)receipt.Source.Kind, ["SourceId"] = receipt.Source.SourceId,
                    ["PlaylistId"] = receipt.PlaylistId, ["PlaylistRevision"] = receipt.PlaylistRevision, ["FeedRevision"] = receipt.FeedRevision
                }
            });
        }

        /// <inheritdoc />
        public bool IsPlaylistReserved(Guid libraryId, Guid playlistId) => LiteDbHostPlaylistStore.IsPlaylistReserved(database, libraryId, playlistId);

        public long Append(SavedHostPlaylist playlist, HostOperationKey origin)
        {
            Check();
            var head = ReadFeedHead(database, playlist.LibraryId);
            var revision = checked(head["CurrentRevision"].AsInt64 + 1);
            AppendFeed(database, head, revision, origin, new BsonArray(FeedUpsert("Playlist", WritePlaylist(playlist))));
            return revision;
        }

        private void Check()
        {
            if (!open || Environment.CurrentManagedThreadId != transactionThread)
                throw new InvalidOperationException("Host repositories may be used only during their owning transaction.");
        }
    }
}
