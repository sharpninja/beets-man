using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Preserves queue order and position by queue occurrence across restarts.</summary>
/// <remarks>2026-11-24: Preserve playback and queue state for FR-PLAYER-001#ac-transport and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
public sealed record PlaybackCheckpoint(Guid SessionId, long CheckpointRevision, long QueueRevision,
    ImmutableArray<QueueEntry> Queue, Guid? ActiveEntryId, TimeSpan Position, bool Shuffle, RepeatMode Repeat,
    PlaylistQueueBinding? PlaylistBacking = null);

/// <summary>Records the playlist source and the last requested auto-save for the current queue.</summary>
/// <remarks>2026-11-24: Preserve playlist provenance and pending replay across restart for FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed record PlaylistQueueBinding(PlaylistVersion Version, string Name, OperationKey? LastSave = null);

/// <summary>Reads and commits detached, versioned playback checkpoints.</summary>
public interface IPlaybackCheckpointStore
{
    /// <summary>Returns the saved checkpoint or an empty successful result for a new session.</summary>
    Task<Outcome<PlaybackCheckpoint?>> ReadCheckpointAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Commits the checkpoint at the expected storage revision.</summary>
    Task<Outcome<PlaybackCheckpoint>> SaveCheckpointAsync(PlaybackCheckpoint checkpoint, long expectedRevision, CancellationToken cancellationToken);
}

/// <summary>Persists the queue checkpoint and its requested playlist publication on a single write lane.</summary>
/// <remarks>2026-11-24: Add the queue publication durability seam for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public interface IQueuePlaylistSaveStore : IPlaybackCheckpointStore
{
    /// <summary>Atomically commits the queue change and its pending playlist publication.</summary>
    Task<Outcome<PlaybackCheckpoint>> StageQueueSaveAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
        PendingPlaylistSave operation, CancellationToken cancellationToken);

    /// <summary>Reads a known operation by origin and operation identity.</summary>
    Task<Outcome<PendingPlaylistSave?>> ReadQueueSaveAsync(OperationKey key, CancellationToken cancellationToken);

    /// <summary>Returns the session's pending publication intents in queue revision order.</summary>
    Task<Outcome<ImmutableArray<PendingPlaylistSave>>> ReadPendingQueueSavesAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Persists a pinned attempt or terminal result while the corresponding operation is still pending.</summary>
    Task<Outcome<PendingPlaylistSave>> RecordQueueSaveAsync(PendingPlaylistSave operation, CancellationToken cancellationToken);
}
