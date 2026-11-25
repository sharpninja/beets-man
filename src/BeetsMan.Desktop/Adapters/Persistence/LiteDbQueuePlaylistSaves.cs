using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Atomically persists queue edits and their pending playlist publications.</summary>
/// <remarks>2026-11-24: Bind the shared playback and operation collections for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IQueuePlaylistSaveStore
{
    /// <inheritdoc />
    public Task<Outcome<PlaybackCheckpoint>> StageQueueSaveAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
        PendingPlaylistSave operation, CancellationToken cancellationToken) => OnLaneAsync(() =>
        Transact(() =>
        {
            if (operation.QueueSource is not { } source || operation.BasePlaylist is not { } playlist ||
                source.SessionId != checkpoint.SessionId || source.QueueRevision != checkpoint.QueueRevision ||
                checkpoint.PlaylistBacking is not { } binding || binding.Version != playlist || binding.Name != operation.Name ||
                binding.LastSave != operation.Key || operation.Status != PendingPlaylistSaveStatus.Pending ||
                operation.DraftId != Guid.Empty || operation.Key.OriginInstanceId == Guid.Empty || operation.Key.OperationId == Guid.Empty ||
                source.ExpectedRevisionPinned != (source.Predecessor is null) || !Corresponds(checkpoint.Queue, operation.Entries))
                return new Outcome<PlaybackCheckpoint>.Invalid([new("invalid-queue-save-intent")]);

            var operations = database.GetCollection<BsonDocument>("pending_operations");
            if (operations.FindById(OperationId(operation.Key)) is not null)
                return new Outcome<PlaybackCheckpoint>.Conflict("operation-intent-mismatch", null);
            if (source.Predecessor is { } key)
            {
                var prior = FindOperation(operations, key);
                if (prior?.QueueSource is not { } from || from.SessionId != source.SessionId ||
                    from.QueueRevision >= source.QueueRevision || prior.BasePlaylist?.PlaylistId != playlist.PlaylistId)
                    return new Outcome<PlaybackCheckpoint>.Conflict("invalid-queue-save-predecessor", null);
            }

            var stored = SaveCheckpoint(checkpoint, expectedRevision);
            if (stored is Outcome<PlaybackCheckpoint>.Success)
                operations.Insert(WriteOperation(operation));
            return stored;
        }, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<PendingPlaylistSave?>> ReadQueueSaveAsync(OperationKey key, CancellationToken cancellationToken) =>
        OnLaneAsync<PendingPlaylistSave?>(() =>
            new Outcome<PendingPlaylistSave?>.Success(FindOperation(database.GetCollection<BsonDocument>("pending_operations"), key)),
            cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<ImmutableArray<PendingPlaylistSave>>> ReadPendingQueueSavesAsync(Guid sessionId, CancellationToken cancellationToken) =>
        OnLaneAsync<ImmutableArray<PendingPlaylistSave>>(() =>
        {
            var operations = database.GetCollection<BsonDocument>("pending_operations");
            var pending = operations.Find(Query.EQ("QueueSessionId", sessionId))
                .Where(row => row["State"].AsString == nameof(PendingPlaylistSaveStatus.Pending)).Select(ReadOperation)
                .OrderBy(operation => operation.QueueSource!.QueueRevision).ToImmutableArray();
            return new Outcome<ImmutableArray<PendingPlaylistSave>>.Success(pending);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<PendingPlaylistSave>> RecordQueueSaveAsync(PendingPlaylistSave operation, CancellationToken cancellationToken) =>
        OnLaneAsync(() => Transact(() => RecordQueueSave(operation), cancellationToken), cancellationToken);

    private Outcome<PendingPlaylistSave> RecordQueueSave(PendingPlaylistSave operation)
    {
        var operations = database.GetCollection<BsonDocument>("pending_operations");
        if (FindOperation(operations, operation.Key) is not { } current || current.QueueSource is not { } source ||
            operation.QueueSource is not { } next || current.Status != PendingPlaylistSaveStatus.Pending ||
            next with { ExpectedRevisionPinned = source.ExpectedRevisionPinned } != source ||
            current.Name != operation.Name || !current.Entries.SequenceEqual(operation.Entries) ||
            current.BasePlaylist?.PlaylistId != operation.BasePlaylist?.PlaylistId)
            return new Outcome<PendingPlaylistSave>.Conflict("queue-save-intent-changed", null);

        if (source.ExpectedRevisionPinned &&
            (!next.ExpectedRevisionPinned || current.BasePlaylist != operation.BasePlaylist))
            return new Outcome<PendingPlaylistSave>.Conflict("queue-save-revision-changed", null);
        if (!source.ExpectedRevisionPinned && next.ExpectedRevisionPinned)
        {
            var prior = source.Predecessor is { } key ? FindOperation(operations, key) : null;
            if (prior?.Receipt is not { } published || operation.BasePlaylist != new PlaylistVersion(published.PlaylistId, published.Revision))
                return new Outcome<PendingPlaylistSave>.Conflict("queue-save-predecessor-unresolved", null);
        }

        if (operation.Status == PendingPlaylistSaveStatus.Accepted &&
            (!next.ExpectedRevisionPinned || operation.Receipt is not { } receipt || operation.BasePlaylist is not { } playlist ||
            receipt.QueueSessionId != next.SessionId || receipt.PlaylistId != playlist.PlaylistId || receipt.Revision <= playlist.Revision))
            return new Outcome<PendingPlaylistSave>.Invalid([new("invalid-queue-save-receipt")]);

        return operations.Update(WriteOperation(operation))
            ? new Outcome<PendingPlaylistSave>.Success(operation)
            : new Outcome<PendingPlaylistSave>.Conflict("queue-save-intent-changed", null);
    }

    private static PendingPlaylistSave? FindOperation(ILiteCollection<BsonDocument> operations, OperationKey key) =>
        operations.FindById(OperationId(key)) is { } row && row["QueueSessionId"].IsGuid ? ReadOperation(row) : null;

    private static bool Corresponds(ImmutableArray<QueueEntry> queue, ImmutableArray<PlaylistEntry> playlist) =>
        !queue.IsDefault && !playlist.IsDefault && queue.Length == playlist.Length &&
        queue.Zip(playlist).All(pair => pair.First.EntryId == pair.Second.EntryId && pair.First.TrackId == pair.Second.TrackId);
}
