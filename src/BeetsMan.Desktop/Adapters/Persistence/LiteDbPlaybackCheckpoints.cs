using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Maps playback checkpoints inside the same validated local LiteDB file.</summary>
/// <remarks>2026-11-24: Add the playback session repository for FR-PLAYER-001#ac-transport and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IPlaybackCheckpointStore
{
    /// <inheritdoc />
    public Task<Outcome<PlaybackCheckpoint?>> ReadCheckpointAsync(Guid sessionId, CancellationToken cancellationToken) =>
        OnLaneAsync<PlaybackCheckpoint?>(() =>
        {
            var row = database.GetCollection<BsonDocument>("playback_sessions").FindById(sessionId);
            return new Outcome<PlaybackCheckpoint?>.Success(row is null ? null : ReadCheckpoint(row));
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<PlaybackCheckpoint>> SaveCheckpointAsync(PlaybackCheckpoint checkpoint, long expectedRevision, CancellationToken cancellationToken) =>
        OnLaneAsync(() => Transact(() => SaveCheckpoint(checkpoint, expectedRevision), cancellationToken), cancellationToken);

    private Outcome<PlaybackCheckpoint> SaveCheckpoint(PlaybackCheckpoint proposed, long expectedRevision)
    {
        if (proposed.SessionId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            proposed.CheckpointRevision != expectedRevision + 1 || proposed.QueueRevision < 0 ||
            proposed.Position < TimeSpan.Zero || (proposed.ActiveEntryId is null && proposed.Position != TimeSpan.Zero) ||
            !Enum.IsDefined(proposed.Repeat) || !ValidQueue(proposed.Queue, proposed.ActiveEntryId))
            return new Outcome<PlaybackCheckpoint>.Invalid([new("invalid-playback-checkpoint")]);

        var sessions = database.GetCollection<BsonDocument>("playback_sessions");
        var existing = sessions.FindById(proposed.SessionId);
        var current = existing is null ? null : ReadCheckpoint(existing);
        if ((current?.CheckpointRevision ?? 0) != expectedRevision)
            return new Outcome<PlaybackCheckpoint>.Conflict("stale-playback-checkpoint", current?.CheckpointRevision ?? 0);
        if (current is not null && (proposed.QueueRevision < current.QueueRevision ||
            (proposed.QueueRevision == current.QueueRevision && !current.Queue.SequenceEqual(proposed.Queue))))
            return new Outcome<PlaybackCheckpoint>.Conflict("stale-playback-queue", current.QueueRevision);

        sessions.Upsert(WriteCheckpoint(proposed));
        return new Outcome<PlaybackCheckpoint>.Success(proposed);
    }

    private static bool ValidQueue(ImmutableArray<QueueEntry> items, Guid? active)
    {
        if (items.IsDefault)
            return false;
        var ids = new HashSet<Guid>();
        return items.All(entry => entry.TrackId != Guid.Empty && entry.EntryId != Guid.Empty && ids.Add(entry.EntryId)) &&
            (active is null || ids.Contains(active.Value));
    }

    private static BsonDocument WriteCheckpoint(PlaybackCheckpoint checkpoint) => new()
    {
        ["_id"] = checkpoint.SessionId, ["CheckpointRevision"] = checkpoint.CheckpointRevision,
        ["QueueRevision"] = checkpoint.QueueRevision, ["PositionTicks"] = checkpoint.Position.Ticks,
        ["ActiveEntryId"] = checkpoint.ActiveEntryId is { } active ? active : BsonValue.Null,
        ["Shuffle"] = checkpoint.Shuffle, ["Repeat"] = checkpoint.Repeat.ToString(),
        ["PlaylistBacking"] = checkpoint.PlaylistBacking is { } backing ? new BsonDocument
        {
            ["PlaylistId"] = backing.Version.PlaylistId, ["Revision"] = backing.Version.Revision, ["Name"] = backing.Name,
            ["LastSave"] = WriteOperationKey(backing.LastSave)
        } : BsonValue.Null,
        ["Queue"] = new BsonArray(checkpoint.Queue.Select(entry =>
            (BsonValue)new BsonDocument { ["EntryId"] = entry.EntryId, ["TrackId"] = entry.TrackId }))
    };

    private static PlaybackCheckpoint ReadCheckpoint(BsonDocument row) => new(
        row["_id"].AsGuid, row["CheckpointRevision"].IsNumber ? row["CheckpointRevision"].AsInt64 : 0,
        row["QueueRevision"].AsInt64,
        [.. row["Queue"].AsArray.Select(item => new QueueEntry(item.AsDocument["EntryId"].AsGuid, item.AsDocument["TrackId"].AsGuid))],
        row["ActiveEntryId"].IsGuid ? row["ActiveEntryId"].AsGuid : null, TimeSpan.FromTicks(row["PositionTicks"].AsInt64),
        row["Shuffle"].IsBoolean && row["Shuffle"].AsBoolean,
        row["Repeat"].IsString ? Enum.Parse<RepeatMode>(row["Repeat"].AsString) : RepeatMode.Off,
        row["PlaylistBacking"].IsDocument ? ReadQueueBinding(row["PlaylistBacking"].AsDocument) : null);

    private static PlaylistQueueBinding ReadQueueBinding(BsonDocument backing) =>
        new(new(backing["PlaylistId"].AsGuid, backing["Revision"].AsInt64), backing["Name"].AsString, ReadOperationKey(backing["LastSave"]));
}
