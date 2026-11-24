# LiteDB repository design

CQRS write handlers use repositories named for the state they own. Query handlers use read-only projection ports with explicit filters and pagination. LiteDB collections, BSON documents, indexes, and mappings belong to the adapters. This boundary serves [CQRS dispatch](cqrs.md), local catalog browsing, and host record replication.

## Projects and database ownership

- `BeetsMan.Desktop.Application` defines desktop repository ports. `BeetsMan.Desktop/Adapters/Persistence` implements them for the desktop's local LiteDB file.
- `BeetsMan.Host/Application` defines host repository ports. `BeetsMan.Host/Adapters/Persistence` implements them for the host's separate LiteDB file.
- Each composition root owns one long-lived direct connection to its own validated file. The desktop accesses authoritative host state through the host API. Each adapter owns its BSON mapper and explicit collection names. [LiteDB connection modes](https://www.litedb.org/docs/connection-string/)
- Store structured state and stable identifiers. Bootstrap JSON, credentials, source audio, and temporary export bytes remain in their respective file or OS stores. Use application-defined IDs as document keys. Keep filenames and paths as data so renames preserve identity.

## Repository contracts

Each write transaction receives a typed group of repositories for that operation. Repositories expose the reads and mutations that the owner needs. Read ports return materialized application snapshots or pages. A caller outside the adapter never receives a database cursor or collection reference.

```csharp
public interface IWriteUnitOfWork<TRepositories>
{
    Task<Outcome<TResult>> ExecuteAsync<TResult>(
        Func<TRepositories, Outcome<TResult>> write,
        CancellationToken cancellationToken);
}

public readonly record struct PlaylistId(Guid Value);

public sealed record PlaylistEntry(Guid EntryId, Guid TrackId);
public sealed record SavedPlaylist(
    PlaylistId Id, string Name, long Revision, IReadOnlyList<PlaylistEntry> Entries);
public sealed record OperationKey(Guid OriginInstanceId, Guid OperationId);
public sealed record SavePlaylistReceipt(PlaylistId PlaylistId, long PlaylistRevision, long FeedRevision);
public sealed record SavedPlaylistOperation(string IntentFingerprint, SavePlaylistReceipt Receipt);

public interface ISavedPlaylistRepository
{
    SavedPlaylist? Find(PlaylistId id);
    void Add(SavedPlaylist playlist);
    bool TryReplace(SavedPlaylist next, long expectedRevision);
}

public interface IPlaylistOperationRepository
{
    SavedPlaylistOperation? Find(OperationKey key);
    void Add(OperationKey key, SavedPlaylistOperation operation);
}

public interface IPlaylistPublicationRepository
{
    long Append(SavedPlaylist playlist, OperationKey origin);
}

public interface IHostPlaylistWriteRepositories
{
    ISavedPlaylistRepository Playlists { get; }
    IPlaylistOperationRepository Operations { get; }
    IPlaylistPublicationRepository Publications { get; }
}

public enum TrackSort { Title, TrackNumber, Length, Rating, Artist, Album, Genre }

public sealed record TrackPageRequest(
    Guid? ArtistId, Guid? AlbumId, string? Filter, TrackSort Sort,
    bool Descending, int Limit, string? Cursor);
public sealed record CatalogTrack(
    Guid Id, Guid ArtistId, Guid AlbumId, string Title, string Artist, string Album,
    int? TrackNumber, TimeSpan? Length, int? Rating, string? Genre, bool AvailableLocally);
public sealed record TrackPage(
    IReadOnlyList<CatalogTrack> Tracks, long ProjectionRevision, string? NextCursor);

public interface ICatalogReadRepository
{
    Task<Outcome<TrackPage>> ReadTracksAsync(
        TrackPageRequest request, CancellationToken cancellationToken);
}
```

These ports show a host playlist write and a desktop catalog read. Desktop playlist drafts use their own repository and local revision. A desktop save persists the pending operation before sending it to the host, and the host result or change feed refreshes the desktop's copy. A host conflict leaves the desktop draft available for resolution. The catalog cursor binds the filter, order, and projection revision for that query. If the projection changes between pages, return a stale-cursor conflict so the caller can restart at page one.

## Transaction and retry rules

1. The handler authorizes and validates the command, then enters the store's write unit of work. It checks for a committed operation receipt before comparing the expected revision against current state. A query returning a document earlier in the workflow provides no write precondition by itself.
2. The LiteDB adapter serializes writes per database file and runs the synchronous callback on a background execution lane. The adapter alone opens the transaction, commits a successful result, and rolls back rejected, cancelled, or failed writes. Repository calls use the same database and thread until the transaction ends. LiteDB v5 transactions are thread-bound. [LiteDB transaction API](https://github.com/litedb-org/LiteDB/blob/v5.0.21/LiteDB/Client/Database/LiteDatabase.cs)
3. The callback performs bounded database work. Network calls, beets execution, staging files, and audio work run outside the transaction. Persist the intent before an external effect. Reconcile the effect before publishing its durable result. A cancellation before commit rolls back. Once a commit succeeds, return or recover the committed receipt on retry.
4. For a host playlist save, find the operation by authenticated origin and operation ID. Identify the intent from the canonical command type and payload, including its expected revision. A fresh correlation ID leaves that identity unchanged. Return the original receipt for the same intent, or a conflict if the intent differs. For a new operation, create or replace the playlist at its expected revision, append the feed revision and delivery records, and persist the receipt in one transaction. Preserve playlist order and entry IDs independently of the track IDs.
5. On the desktop, apply a contiguous host change page and save its cursor in the same transaction. A duplicate page has no further effect. A gap requests catch-up. Acknowledge only after commit. Snapshot replacement changes the synchronized projection and its cursor in one publication step. Local drafts, pending operations, and preferences live in their own collections.

## Collection ownership

The [Structurizr entity definitions](litedb-entities.dsl) give each collection its document key, fields, and logical references. The [desktop entity view](views.md#desktoplitedbentities) and [host entity view](views.md#hostlitedbentities) sit beneath their corresponding C4 datastore containers. Arrows describe application references or transaction groupings, with relationships enforced by the owning repositories.

| Database | Collections | Owner and access pattern |
| --- | --- | --- |
| Desktop | `catalog_artists`, `catalog_albums`, `catalog_tracks`, `catalog_playlists` | Replication writes the synchronized projection. Catalog queries use stable IDs, indexed artist and album lookups, and paged normalized search and sort with a stable ID tie-break. |
| Desktop | `listening_records`, `shared_preference_cache` | Replication maintains local history, favorites, and the last valid shared preferences. Separate local overrides and pending operations retain their owners and revisions. |
| Desktop | `playlist_drafts`, `playback_sessions`, `preference_overrides` | Playlist, playback, and settings handlers own their respective documents. Scope keys to the draft, session, or registered setting. |
| Desktop | `pending_operations`, `sync_checkpoints` | Replay reads pending operations by origin and operation ID. Replication records progress by library and feed. |
| Host | `catalog_artists`, `catalog_albums`, `catalog_tracks`, `saved_playlists`, `listening_records`, `shared_preferences` | The host publishes catalog and user state. Use stable entity IDs and owner revisions. |
| Host | `registered_instances`, `library_jobs`, `operation_receipts` | Authentication resolves instance identity. Jobs retain operation identity and status for recovery. Receipts resolve retries by authenticated instance and operation ID. |
| Host | `revision_heads`, `revision_changes`, `notification_deliveries` | Publication advances the library head and appends corresponding changes and delivery records together. Delivery reads by recipient and pending revision. |
| Host | `device_exports`, `device_export_entries` | Export management owns device/share/profile metadata and looks up owned files by export identity and normalized destination path. |
| Both | `store_schema` | Bootstrap checks store identity and schema version before binding repositories. |

The adapter declares its collection names, indexes, and projections next to its mappings. Use the document primary key for identity. Use explicit stable composite keys for operation receipts, feed revisions, and destination paths where identity spans fields. Check that indexes support the actual paged queries and preserve ordering when keys tie. When a response combines rows and a revision or cursor, read both through a consistent store snapshot. Complete each database read before returning its page. The host change feed uses its captured upper revision as specified in [host and replication contracts](../stage-0/contracts.md#host-and-replication). [LiteDB index semantics](https://www.litedb.org/docs/indexes/)

Open each store only after the bootstrap path checks pass. Apply ordered repository schema migrations before normal use, and advance the recorded schema version only after the migration and its integrity checks succeed. Keep migrations restartable and surface an unsupported newer version through diagnostics. Keep database files outside Syncthing shares.

## Verification seams

Exercise CQRS handlers with repository fakes and the same application contracts used by the LiteDB adapters. Cover stale expected revisions, repeated operations with identical or different intent, cancellation before commit, and a rejected write after the first collection update. Use disposable LiteDB files to verify rollback across collections, durable receipt lookup after reopen, coherent ordering and paging, and atomic change-page checkpoints. Keep these cases within the existing dispatch, catalog, and record-sync test groups. [Stage 0 test specifications](../testing/ui/index.md)
