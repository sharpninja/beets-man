using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Applies contiguous catalog revisions and their checkpoint in the same desktop transaction.</summary>
/// <remarks>2026-11-24: Preserve local ownership, drafts and files while applying upserts and tombstones for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
/// <remarks>2026-11-25: Apply shared listening records and defaults on the same lane for FR-RECORDS-001#ac-replicate.</remarks>
/// <remarks>2026-11-25: Keep idle automatic pulls from rewriting a bound checkpoint for FR-RECORDS-001#ac-notify.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : ICatalogReplicaStore
{
    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> ReadReplicaAsync(Guid library, Guid instance, CancellationToken token) =>
        OnLaneAsync(() => ReadReplicaState(library, instance, CatalogCheckpoint(library)), token);

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> ApplyCatalogPageAsync(CatalogFeedPage page, CancellationToken token) =>
        OnLaneAsync(() => Transact<ReplicationState>(() =>
        {
            if (!CatalogFeedProtocol.IsValid(page))
                return new Outcome<ReplicationState>.Invalid([new("invalid-catalog-page")]);
            var library = page.LibraryId;
            var instance = page.RecipientInstanceId;
            var saved = CatalogCheckpoint(library);
            var previous = ReadReplicaState(library, instance, saved);
            if (previous is not Outcome<ReplicationState>.Success { Value: var state })
                return previous;
            if (page.AfterRevision != state.Cursor)
                return page.ThroughRevision <= state.Cursor ? previous : new Outcome<ReplicationState>.Conflict("stale-replica-cursor", state.Cursor);
            if (page.Revisions.IsEmpty && !state.SnapshotRequired && saved?["OriginInstanceId"].IsGuid == true)
                return previous;
            foreach (var revision in page.Revisions)
            {
                foreach (var change in revision.Changes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ApplyCatalogChange(library, revision.Revision, change))
                        return new Outcome<ReplicationState>.Conflict("catalog-record-scope-conflict", state.Cursor);
                }
            }
            return SaveReplicaCheckpoint(library, instance, page.ThroughRevision, snapshotRequired: false, saved);
        }, token), token);

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> RequireCatalogSnapshotAsync(Guid library, Guid instance, long cursor, CancellationToken token) =>
        OnLaneAsync(() => Transact<ReplicationState>(() =>
        {
            var checkpoint = CatalogCheckpoint(library);
            var current = ReadReplicaState(library, instance, checkpoint);
            if (current is not Outcome<ReplicationState>.Success { Value: var state })
                return current;
            return state.Cursor == cursor ? SaveReplicaCheckpoint(library, instance, cursor, snapshotRequired: true, checkpoint) :
                new Outcome<ReplicationState>.Conflict("stale-replica-cursor", state.Cursor);
        }, token), token);

    private BsonDocument? CatalogCheckpoint(Guid library) => database.GetCollection<BsonDocument>("sync_checkpoints").FindById($"{library:N}/catalog");

    private Outcome<ReplicationState> ReadReplicaState(Guid library, Guid instance, BsonDocument? checkpoint)
    {
        if (library == Guid.Empty || instance == Guid.Empty || checkpoint is not null && checkpoint["OriginInstanceId"].IsGuid &&
            checkpoint["OriginInstanceId"].AsGuid != instance)
            return new Outcome<ReplicationState>.Invalid([new("replica-scope-unavailable")]);
        var cursor = checkpoint?["AppliedRevision"].AsInt64 ?? 0;
        var pending = PendingRecords().Find(Query.And(Query.EQ("OriginInstanceId", instance), Query.EQ("State", nameof(PendingPlaylistSaveStatus.Pending))))
            .Count(row => row["LibraryId"].IsGuid ? row["LibraryId"].AsGuid == library : row["CommandType"].AsString == nameof(SavePlaylist));
        return new Outcome<ReplicationState>.Success(new(library, instance, cursor, cursor, pending,
            checkpoint?["SnapshotRequired"].IsBoolean == true && checkpoint["SnapshotRequired"].AsBoolean, ReadListenerConflicts(library, instance),
            checkpoint?["LastSnapshotId"].IsGuid == true ? checkpoint["LastSnapshotId"].AsGuid : null));
    }

    /// <summary>Commits the cursor and optional replacement identity for FR-RECORDS-001#ac-replicate and ac-offline.</summary>
    private Outcome<ReplicationState> SaveReplicaCheckpoint(Guid library, Guid instance, long cursor, bool snapshotRequired, BsonDocument? checkpoint,
        Guid? snapshotId = null)
    {
        checkpoint ??= new BsonDocument { ["_id"] = $"{library:N}/catalog", ["LibraryId"] = library, ["Feed"] = "catalog" };
        checkpoint["OriginInstanceId"] = instance;
        checkpoint["AppliedRevision"] = cursor;
        checkpoint["SnapshotRequired"] = snapshotRequired;
        if (snapshotId is { } replacement)
            checkpoint["LastSnapshotId"] = replacement;
        database.GetCollection<BsonDocument>("sync_checkpoints").Upsert(checkpoint);
        return ReadReplicaState(library, instance, checkpoint);
    }

    private bool ApplyCatalogChange(Guid library, long revision, CatalogFeedChange change)
    {
        var table = CatalogProjectionTable(change.Kind);
        var existing = table.FindById(change.RecordId);
        var retainedRecord = change.Kind is CatalogEntityKind.Favorite or CatalogEntityKind.Play or CatalogEntityKind.SharedDefault;
        if (existing is not null && (!existing["LibraryId"].IsGuid || existing["LibraryId"].AsGuid != library ||
            retainedRecord && existing["Kind"].AsString != change.Kind.ToString()))
            return false;
        if (retainedRecord && existing is not null && existing["SourceRevision"].AsInt64 > revision)
            return true;
        var row = new BsonDocument { ["_id"] = change.RecordId, ["LibraryId"] = library, ["SourceRevision"] = revision };
        if (retainedRecord)
            row["Kind"] = change.Kind.ToString();
        if (change.Operation == CatalogEntityOperation.Delete)
        {
            if (retainedRecord)
            {
                row["Deleted"] = true;
                table.Upsert(row);
            }
            else if (existing is not null)
                table.Delete(change.RecordId);
            return true;
        }
        switch (change.Entity)
        {
            case ReplicaArtist artist:
                row["Name"] = artist.Name;
                row["SortName"] = artist.SortName;
                break;
            case ReplicaAlbum album:
                row["Title"] = album.Title;
                row["SortTitle"] = album.SortTitle;
                row["AlbumArtistId"] = Ref(album.AlbumArtistId);
                break;
            case ReplicaTrack track:
                row["Title"] = track.Title;
                row["SortTitle"] = track.SortTitle;
                row["RelativePath"] = track.RelativePath;
                row["ArtistId"] = Ref(track.ArtistId);
                row["AlbumId"] = Ref(track.AlbumId);
                row["Genre"] = track.Genre;
                row["TrackNumber"] = Number(track.TrackNumber);
                row["DiscNumber"] = Number(track.DiscNumber);
                row["Year"] = Number(track.Year);
                row["Rating"] = Number(track.Rating);
                row["DurationTicks"] = track.DurationTicks is { } ticks ? new BsonValue(ticks) : BsonValue.Null;
                if (track.HasAlbumTag)
                    row["TagAlbum"] = track.AlbumTag;
                if (track.HasAlbumArtistTag)
                    row["TagAlbumArtist"] = track.AlbumArtistTag;
                break;
            case ReplicaPlaylist playlist:
                row["Name"] = playlist.Name;
                row["PlaylistRevision"] = playlist.PlaylistRevision;
                row["Entries"] = new BsonArray(playlist.Entries.Select(entry => (BsonValue)new BsonDocument
                    { ["EntryId"] = entry.EntryId, ["TrackId"] = entry.TrackId }));
                break;
            case ReplicaFavorite favorite:
                row["TrackId"] = favorite.TrackId;
                row["ListenerId"] = library;
                break;
            case ReplicaPlay play:
                row["TrackId"] = play.TrackId;
                row["ListenerId"] = library;
                row["PlayedAtUtcTicks"] = play.PlayedAtUtc.UtcTicks;
                break;
            case ReplicaSharedDefault setting:
                row["PreferenceKey"] = setting.Key;
                row["RegistryVersion"] = setting.RegistryVersion;
                if (setting.DurationTicks is { } duration)
                    row["DurationTicks"] = duration;
                if (setting.Flag is { } flag)
                    row["Flag"] = flag;
                if (setting.Text is { } text)
                    row["Text"] = text;
                break;
        }
        table.Upsert(row);
        return true;

        static BsonValue Ref(Guid? value) => value is { } id ? new BsonValue(id) : BsonValue.Null;
        static BsonValue Number(int? value) => value is { } number ? new BsonValue(number) : BsonValue.Null;
    }

    /// <summary>Resolves only synchronized catalog tables for FR-RECORDS-001#ac-replicate.</summary>
    private ILiteCollection<BsonDocument> CatalogProjectionTable(CatalogEntityKind kind) => database.GetCollection<BsonDocument>(kind switch
    {
        CatalogEntityKind.Artist => "catalog_artists",
        CatalogEntityKind.Album => "catalog_albums",
        CatalogEntityKind.Track => "catalog_tracks",
        CatalogEntityKind.Playlist => "catalog_playlists",
        CatalogEntityKind.Favorite or CatalogEntityKind.Play => "listening_records",
        CatalogEntityKind.SharedDefault => "shared_preference_cache",
        _ => throw new InvalidDataException("The validated catalog entity kind is unknown.")
    });
}
