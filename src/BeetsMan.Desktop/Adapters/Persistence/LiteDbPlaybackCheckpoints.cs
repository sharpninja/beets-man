using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Maps playback checkpoints inside the same validated local LiteDB file.</summary>
/// <remarks>2026-11-24: Add the playback session repository for FR-PLAYER-001#ac-transport and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
/// <remarks>2026-11-24: Reopen the exact local staged queue save for FR-LIBRARY-002#ac-batch-local.</remarks>
/// <remarks>2026-11-25: Commit a new play and its playback checkpoint together for FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IPlaybackHistoryCheckpointStore
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

    /// <inheritdoc />
    public Task<Outcome<PlaybackCheckpoint>> SaveCheckpointWithPlayAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
        LibraryRecordScope scope, RecordPlay play, CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        if (scope.LibraryId == Guid.Empty || scope.OriginInstanceId == Guid.Empty || play.PlayId == Guid.Empty || play.TrackId == Guid.Empty ||
            play.PlayedAtUtc.Offset != TimeSpan.Zero || play.PlayedAtUtc.UtcTicks == 0)
            return new Outcome<PlaybackCheckpoint>.Invalid([new("invalid-playback-history")]);
        var saved = SaveCheckpoint(checkpoint, expectedRevision);
        if (saved is not Outcome<PlaybackCheckpoint>.Success)
            return saved is Outcome<PlaybackCheckpoint>.Conflict { Code: "stale-playback-checkpoint" } &&
                IsCommittedPlaybackPlay(checkpoint, scope, play) ? new Outcome<PlaybackCheckpoint>.Success(checkpoint) : saved;
        var recorded = StageListenerPlay(scope, play.PlayId, play);
        return recorded switch
        {
            Outcome<RecordedPlay>.Success => saved,
            Outcome<RecordedPlay>.Conflict conflict => new Outcome<PlaybackCheckpoint>.Conflict(conflict.Code, conflict.ActualRevision),
            _ => throw new InvalidOperationException("Unexpected local listening stage outcome.")
        };
    }, token), token);

    /// <summary>Recognizes the already committed checkpoint and play when their local receipt was lost.</summary>
    /// <remarks>2026-11-25: Retry the same observed play for FR-RECORDS-001#ac-offline.</remarks>
    private bool IsCommittedPlaybackPlay(PlaybackCheckpoint checkpoint, LibraryRecordScope scope, RecordPlay play)
    {
        var row = database.GetCollection<BsonDocument>("playback_sessions").FindById(checkpoint.SessionId);
        var record = PendingRecords().FindById(ListenerKey(scope, play.PlayId));
        if (row is null || record is null ||
            !MatchesIntent(record, scope, SerializeIntent(new LibraryRecordIntent.Play(play.PlayId, play.TrackId, play.PlayedAtUtc))))
            return false;
        var committed = ReadCheckpoint(row);
        return committed == (checkpoint with { Queue = committed.Queue }) && committed.Queue.SequenceEqual(checkpoint.Queue);
    }

    private Outcome<PlaybackCheckpoint> SaveCheckpoint(PlaybackCheckpoint proposed, long expectedRevision)
    {
        if (proposed.SessionId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            proposed.CheckpointRevision != expectedRevision + 1 || proposed.QueueRevision < 0 ||
            proposed.Position < TimeSpan.Zero || (proposed.ActiveEntryId is null && proposed.Position != TimeSpan.Zero) ||
            !Enum.IsDefined(proposed.Repeat) || !ValidQueue(proposed.Queue, proposed.ActiveEntryId) || !ValidActivePlay(proposed))
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

    /// <summary>Verifies that the restorable play belongs to the exact active queue occurrence.</summary>
    private static bool ValidActivePlay(PlaybackCheckpoint proposed) => proposed.ActivePlay is not { } play ||
        play.PlayId != Guid.Empty && play.QueueEntryId is Guid entry && entry == proposed.ActiveEntryId &&
        proposed.Queue.Any(item => item.EntryId == entry && item.TrackId == play.TrackId);

    private static BsonDocument WriteCheckpoint(PlaybackCheckpoint checkpoint) => new()
    {
        ["_id"] = checkpoint.SessionId, ["CheckpointRevision"] = checkpoint.CheckpointRevision,
        ["QueueRevision"] = checkpoint.QueueRevision, ["PositionTicks"] = checkpoint.Position.Ticks,
        ["ActiveEntryId"] = checkpoint.ActiveEntryId is { } active ? active : BsonValue.Null,
        ["Shuffle"] = checkpoint.Shuffle, ["Repeat"] = checkpoint.Repeat.ToString(),
        ["WasPlaying"] = checkpoint.WasPlaying,
        ["ActivePlay"] = checkpoint.ActivePlay is { } play ? new BsonDocument { ["PlayId"] = play.PlayId, ["TrackId"] = play.TrackId,
            ["QueueEntryId"] = play.QueueEntryId!.Value } : BsonValue.Null,
        ["PlaylistBacking"] = checkpoint.PlaylistBacking is { } backing ? new BsonDocument
        {
            ["PlaylistId"] = backing.Version.PlaylistId, ["Revision"] = backing.Version.Revision, ["Name"] = backing.Name,
            ["LastSave"] = WriteOperationKey(backing.LastSave),
            ["StagedSave"] = backing.StagedSave is { } staged ? new BsonDocument
            {
                ["OperationId"] = staged.OperationId, ["BatchId"] = staged.Version.BatchId,
                ["Revision"] = staged.Version.Revision, ["Position"] = staged.Position
            } : BsonValue.Null
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
        row["PlaylistBacking"].IsDocument ? ReadQueueBinding(row["PlaylistBacking"].AsDocument) : null,
        row["ActivePlay"].IsDocument ? ReadActivePlay(row["ActivePlay"].AsDocument) : null,
        row["WasPlaying"].IsBoolean && row["WasPlaying"].AsBoolean);

    private static PlaybackPlayOccurrence ReadActivePlay(BsonDocument row) => new(row["PlayId"].AsGuid, row["TrackId"].AsGuid, row["QueueEntryId"].AsGuid);

    private static PlaylistQueueBinding ReadQueueBinding(BsonDocument backing) =>
        new(new(backing["PlaylistId"].AsGuid, backing["Revision"].AsInt64), backing["Name"].AsString, ReadOperationKey(backing["LastSave"]),
            backing["StagedSave"].IsDocument ? ReadQueueBatchSave(backing["StagedSave"].AsDocument) : null);

    private static LibraryBatchStageReceipt ReadQueueBatchSave(BsonDocument row) =>
        new(row["OperationId"].AsGuid, new(row["BatchId"].AsGuid, row["Revision"].AsInt64), row["Position"].AsInt32);
}
