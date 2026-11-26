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

After a successful incremental catalog refresh, the registered desktop resumes pending direct named saves and queue auto-saves for its origin. It reuses the stored operation IDs, expected versions, names and ordered entries. Batch mode pauses these direct sends and keeps them outside the batch review. A host version conflict records the observed revision and preserves the local draft.

With queue auto-save enabled, the desktop commits the changed queue and the exact ordered playlist save in one LiteDB transaction. Each later pending edit refers to the preceding operation. After that operation publishes, the desktop pins the next edit to the playlist revision in the host receipt before sending it. A dependency conflict preserves the local queue and its intended playlist order for resolution. The effective toggle comes from the session owner.

The version 1 playlist save endpoint scopes the request to the verified instance and library. The host checks registration again inside the write transaction, before returning even a previously committed receipt. The receipt carries the originating draft or queue session, playlist revision, and library feed revision.

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
| Desktop | `catalog_artists`, `catalog_albums`, `catalog_tracks`, `catalog_playlists` | Replication writes the synchronized projection. Catalog queries use stable IDs, indexed artist and album lookups, and paged normalized search and sort with a stable ID tie-break. The originating desktop reads pending maintenance on the same store lane and applies its membership and display paths during queries. |
| Desktop | `listening_records`, `shared_preference_cache` | Shared records apply with the feed cursor and replace the library's projection at the end of a complete snapshot. Stable IDs, play timestamps, and favorite or default deletion revisions survive replacement. Local overrides and pending operations retain their owners and revisions. |
| Desktop | `plugin_registry` | The local registry keeps one version and archive digest per admitted package identity, plus its protocol range, entry point, declared contributions, trust, and enablement. The revision advances with the entire mutation, including disabling the earlier enabled version. Registry rows remain outside the replicated library projection. |
| Desktop | `shortcut_bindings` | The device-local singleton keeps the contextual overrides and their revision. Each write replaces the set atomically. Built-ins remain in the shortcut catalog, and accepted OS registrations belong to the active workspace. |
| Desktop | `playlist_drafts`, `playback_sessions`, `preference_overrides` | Scope keys to the draft, session, or registered setting. The playback checkpoint keeps the active play ID with its track and queue entry, the position, and whether the primary deck was playing. An older checkpoint without `WasPlaying` restores in the stopped state. A confirmed start saves its checkpoint and pending history operation atomically. A cleared preference override retains its local revision so a stale settings edit can be rejected. |
| Desktop | `pending_operations`, `sync_checkpoints` | Replay scopes listener intents to the library and originating desktop. A durable scoped counter assigns chronological order with each staged operation. Later favorites pin the predecessor's published revision before transfer. A conflicting draft retains its original operation and host revision until an explicit decision. The catalog cursor remains scoped by library and feed. |
| Desktop | `library_batch_modes`, `library_batch_entries`, `library_batch_receipts` | The local batch repository scopes the toggle to a library and originating desktop. It stores typed changes in review order, pins the approved revision before publication, and removes staged payloads when the batch is reverted or confirmed. The commit transaction also records each queue stage's published playlist version and command identity. The playback checkpoint can resolve that receipt after restart. |
| Desktop | `library_batch_import_copies` | Each scope and operation ID claims a private import folder before files are written. Startup recovery or batch resolution releases the files once the claim no longer points to an active staged operation. Ordered file IDs, names, lengths, and digests are in the staged entry. |
| Host | `library_maintenance_plans` | Persists the privately scoped preview, selected catalog revision, ordered native IDs and paths, and exact catalog or file effects. An audio-removal plan also stores the approved paths, lengths and SHA-256 fingerprints. A confirmation assigns it to a direct job or an approved batch in the same transaction as its receipt. |
| Host | `library_batch_publications`, `library_batch_publication_entries` | Stores the owning desktop, approved review, entry order, pinned native targets, and original receipt. Child operation IDs and maintenance plans are reserved in the admission transaction. The running journal stores the next position, native attempt marker, and reservations for affected existing playlists. Per-entry manifests record organization source bytes and verified tag fingerprints needed by later organization or deletion. The reserved plan keeps the reviewed file paths for explicit audio deletion. Playlist-only and supported native batches publish the final catalog and playlist projection in one feed revision. |
| Host | `library_batch_import_parts` | Records the verified file under its approval, entry, and ordinal. The copy lives in private batch staging. The host returns these receipts on replay and keeps them separate from direct import uploads. |
| Host | `library_batch_publications`, `operation_receipts` | A withdrawal enters the terminal `Withdrawn` phase on the same transaction lane as approval admission and native start. If the approval has yet to arrive, the host reserves the batch and operation IDs and records only their review identity. Replaying a delayed submission cannot admit it or advance the catalog feed. |
| Host | `library_batch_publication_entries`, `operation_receipts` | A batch import entry owns its public review, private native match identities and increasing review revision. Its explicit choice and replay receipt commit together under the authenticated desktop's operation ID. Matched entries record the prepared copy paths and hashes before execution. The running journal fences the native attempt. Album and track readback advance the cursor in the same transaction. Skip records no native evidence, and an all-skip batch retains the current feed head. |
| Host | `catalog_artists`, `catalog_albums`, `catalog_tracks`, `saved_playlists`, `listening_records`, `shared_preferences` | The host publishes catalog and user state. Use stable entity IDs and owner revisions. |
| Host | `listening_records`, `shared_preferences`, `operation_receipts` | Favorites share one listener profile per library. Played-at UTC ticks and track ID are immutable for a play ID. Shared defaults accept duration ticks for automatic fade, a Boolean for sync-completion alerts, and text for the Appearance theme. Clearing a default writes a tombstone. The record revision matches the feed revision on change. An identical replay returns its original receipt. |
| Host | `registered_instances`, `library_jobs`, `operation_receipts` | Authentication resolves instance identity. Android registration supplies the export share and receiver endpoint, and staging rechecks that registration before committing. Receiver credentials are stored separately. Jobs retain operation identity and status for recovery. Organization jobs also pin the ordered track, destination, length, and SHA-256 before native work. Receipts resolve retries by authenticated instance and operation ID. |
| Host | `registered_instances`, `desktop_registry_heads`, `operation_receipts` | Desktop rows keep display name, access role, active or revoked status, and an optional Last seen UTC timestamp. Enrollment and revocation compare the library's registry revision and commit the row, revision, and original response together. Last seen advances independently and remains absent on legacy rows until the first observed request. |
| Host | `beets_library_setup`, `operation_receipts` | An admitted initialization pins the originating desktop, operation, requested paths and settings fingerprint. Native preparation stores both artifact hashes before installation. The setup revision and original receipt commit after both provisioned files verify. A later configuration edit stores the typed settings and original receipt at the next revision, which native jobs load when preparing their private configuration. |
| Host | `import_uploads` | Stores the ordered file names, lengths and SHA-256 digests by library, registered desktop and upload ID. The accepted job, upload-to-job link and submission receipt commit together after all copies verify. |
| Host | `revision_heads`, `revision_changes`, `notification_deliveries` | Publication advances the library head and appends corresponding changes and delivery records together. Catalog-only removal publishes each deleted track's ID, library ID, and removal revision, with the original operation on the change page. The authenticated pull and acknowledgement operate on one recipient's rows. |
| Host | `catalog_replica_snapshots`, `catalog_replica_snapshot_rows` | Capture the authenticated recipient's catalog at one upper revision. Store ordered entity rows under that scope. The next capture for the same recipient supersedes the earlier capture. |
| Desktop | `catalog_snapshot_stages`, `catalog_snapshot_stage_rows`, `sync_checkpoints` | Stage bounded pages separately from the visible catalog. Complete replacement writes the synchronized records and checkpoint together. Store the completed snapshot ID for retry after an interrupted acknowledgement. |
| Host | `device_exports`, `device_export_entries` | Each staged edit persists the selected profile and share, ordered entries and its operation receipt together. Audio rows with `IsDirect` retain explicit selection when a playlist is removed. Legacy rows with no flag remain playlist dependencies. A new staged revision invalidates prior preview approval. The preview binds playlist and audio payloads by digest before the publication journal can apply them. |
| Host | `device_export_manifests`, `device_export_owned_files` | Publication ownership is keyed to the library, device and share across layout changes. The manifest holds the active profile version. Owned paths carry the digest and source ID of the committed file. Review keeps the active profile and shared files intact. Publication commits the approved assignment, corrected ordered entries, playlist digests and ownership together. |
| Host | `device_export_scans` | A publication with file effects queues its sender scan in the same transaction as the ownership and publication receipt. The transfer ID matches the publication ID. A time-limited lease and recorded retry time recover an interrupted scan. Sender acknowledgement advances the checkpoint to awaiting the receiver. |
| Host | `device_export_scan_items` | The queued transfer retains each published destination and removed path. The receiver observer verifies the reviewed sender bytes and records their stable Syncthing index versions. Progress counts only matching receiver-local versions. A transfer completes after all writes and deletions are verified. |
| Host | `pc_mirror_states` | The library projection retains the last verified file count and pause status for the configured native scope, plus the control revision and connection generation. A command records its operation key before touching Syncthing. Final status and the original response commit together in the state and receipt collections. A newer command can supersede an unfinished intent at the current revision. |

| Host | `device_export_publication_journal`, `device_export_publication_items` | Reserve one operation per share and persist its ordered file actions, ownership projection, and optional replacement profile before applying effects. Progress advances after verified steps. Commit the ownership revision, profile assignment, plan state and original receipt together and clear the journal. |
| Both | `store_schema` | Bootstrap checks store identity and schema version before binding repositories. |

Transfer inspection projects the registered library, device and folder as a stable media share ID, including for older Syncthing folder IDs that are strings. Retrying an eligible checkpoint advances its revision, makes that work immediately due, and stores the original response in `operation_receipts`. A response lost after commit can be replayed without initiating a new file publication.

Pairing reserves the authenticated operation in `operation_receipts` and records its pending key on the selected `registered_instances` row before the sender offer. After verification, the host clears that key and commits the directory revision and original pairing result together. For Android it retains the approved receiver root and sender identity, the initial profile ID and revision, and whether the receiver had accepted at the time of the offer. A status query updates the latest receiver phase and failure code with the directory revision when the observation changes. The published layout assignment takes precedence once a publication exists. Older registrations can continue to use their existing export bindings and the default layout.

The adapter declares its collection names, indexes, and projections next to its mappings. Use the document primary key for identity. Use explicit stable composite keys for operation receipts, feed revisions, and destination paths where identity spans fields. Check that indexes support the actual paged queries and preserve ordering when keys tie. When a response combines rows and a revision or cursor, read both through a consistent store snapshot. Complete each database read before returning its page. The host change feed uses its captured upper revision as specified in [host and replication contracts](../stage-0/contracts.md#host-and-replication). [LiteDB index semantics](https://www.litedb.org/docs/indexes/)

Open each store only after the bootstrap path checks pass. Version 1 is the initial persisted schema for both roles. Its readers supply defaults for documented optional fields from earlier version 1 records. Bootstrap rejects an unsupported schema and preserves the existing file for repair. When a future version needs a migration, run ordered repository migrations before normal use and advance the recorded version after the migration and integrity checks commit. Keep migrations restartable and keep database files outside Syncthing shares.

## Verification seams

Exercise CQRS handlers with repository fakes and the same application contracts used by the LiteDB adapters. Cover stale expected revisions, repeated operations with identical or different intent, cancellation before commit, and a rejected write after the first collection update. Use disposable LiteDB files to verify rollback across collections, durable receipt lookup after reopen, coherent ordering and paging, and atomic change-page checkpoints. Keep these cases within the existing dispatch, catalog, and record-sync test groups. [Stage 0 test specifications](../testing/ui/index.md)
