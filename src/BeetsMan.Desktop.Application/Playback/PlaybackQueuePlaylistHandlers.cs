using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Coordinates playlist backing, ordered queue edits, and durable auto-save receipts.</summary>
/// <remarks>2026-11-24: Implement FR-PLAYER-001#ac-queue-autosave and preserve retries under FR-RECORDS-001#ac-offline.</remarks>
/// <remarks>2026-11-24: Present and recover local queue saves for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed partial class PlaybackSessionHandlers : ICommandHandler<LoadPlaylistQueue, PlaybackState>,
    ICommandHandler<EditQueue, PlaybackState>, ICommandHandler<SetQueueAutoSave, PlaybackState>
{
    private PlaylistQueueBinding? playlistBinding;

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(LoadPlaylistQueue command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ct => LoadQueueAsync(command, ct), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(EditQueue command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ct => EditQueueAsync(command, context, ct), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(SetQueueAutoSave command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, _ => Task.FromResult(ToggleAutoSave(command)), token);

    /// <summary>Replays pending saves through the playlist owner when a known host becomes available.</summary>
    /// <remarks>2026-11-25: Release the playback lane before publication so FR-PLAYER-001#ac-transport can run while ac-queue-autosave awaits the host.</remarks>
    public async Task<Outcome<PlaybackState>> ReplayQueuePlaylistSavesAsync(Guid sessionId, RequestContext context, CancellationToken token)
    {
        var ready = await RunAsync(sessionId, _ => Task.FromResult<Outcome<PlaybackState>>(new Outcome<PlaybackState>.Success(state)), token)
            .ConfigureAwait(false);
        if (ready is not Outcome<PlaybackState>.Success || playlistPublication is null)
            return ready;

        try { await playlistPublication.ReplayAsync(sessionId, context, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PlaybackState>.Cancelled(); }

        return await RunAsync(sessionId, async ct =>
        {
            await RefreshSaveStatusAsync(ct).ConfigureAwait(false);
            return new Outcome<PlaybackState>.Success(state);
        }, token).ConfigureAwait(false);
    }

    /// <summary>Loads the saved order and binds the queue to the verified playlist version and name.</summary>
    /// <remarks>2026-11-25: Include the loaded playlist name for FR-PLAYER-001#ac-queue-autosave.</remarks>
    private async Task<Outcome<PlaybackState>> LoadQueueAsync(LoadPlaylistQueue command, CancellationToken token)
    {
        if (playlistPublication is null)
            return Invalid("queue-playlists-unavailable");
        var result = await playlistPublication.ReadPlaylistAsync(command.PlaylistId, token).ConfigureAwait(false);
        if (result is not Outcome<PublishedPlaylist>.Success found)
            return CopyFailure(result);
        var playlist = found.Value;
        if (playlist.Version != new PlaylistVersion(command.PlaylistId, command.ExpectedPlaylistRevision))
            return Conflict("stale-saved-playlist", playlist.Version.Revision);
        if (playlistBinding?.Version is { } known && known.PlaylistId == playlist.Version.PlaylistId && known.Revision > playlist.Version.Revision)
            return Conflict("stale-saved-playlist", known.Revision);
        if (string.IsNullOrWhiteSpace(playlist.Name) || playlist.Entries.IsDefault ||
            playlist.Entries.Any(entry => entry.TrackId == Guid.Empty || entry.EntryId == Guid.Empty) ||
            playlist.Entries.Select(entry => entry.EntryId).Distinct().Count() != playlist.Entries.Length)
            return Invalid("invalid-cached-playlist");

        var binding = new PlaylistQueueBinding(playlist.Version, playlist.Name);
        if (playlistBinding is { LastSave: { } key } prior && prior.Version == playlist.Version)
        {
            var last = await playlistPublication.Store.ReadQueueSaveAsync(key, token).ConfigureAwait(false);
            if (last is not Outcome<PendingPlaylistSave?>.Success save)
                return CopyFailure(last);
            // 2026-11-25: A receipt can arrive during the cached read for FR-PLAYER-001#ac-queue-autosave.
            if (save.Value?.Receipt is { } receipt && receipt.Revision > playlist.Version.Revision)
                return Conflict("stale-saved-playlist", receipt.Revision);
            if (save.Value?.Status == PendingPlaylistSaveStatus.Pending)
                binding = binding with { LastSave = key };
        }
        var queue = state.Queue with
        {
            QueueRevision = checked(state.Queue.QueueRevision + 1), BackingPlaylist = playlist.Version,
            BackingPlaylistName = binding.Name,
            Entries = [.. playlist.Entries.Select(entry => new QueueEntry(entry.EntryId, entry.TrackId))],
            AutoSave = playlistBinding?.Version.PlaylistId == playlist.Version.PlaylistId ? state.Queue.AutoSave : initialAutoSave,
            SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null, BatchSave = null
        };
        var next = WithQueue(queue) with { Revision = checked(state.Revision + 1) };
        var checkpoint = MakeCheckpoint(next, binding);
        var write = await playlistPublication.Store.SaveCheckpointAsync(checkpoint, checkpointRevision, token).ConfigureAwait(false);
        if (write is not Outcome<PlaybackCheckpoint>.Success saved)
            return CopyFailure(write);
        playlistBinding = binding;
        checkpointRevision = saved.Value.CheckpointRevision;
        state = next;
        return new Outcome<PlaybackState>.Success(state);
    }

    private Outcome<PlaybackState> ToggleAutoSave(SetQueueAutoSave command)
    {
        if (playlistBinding is null)
            return Invalid("queue-has-no-playlist");
        if (command.ExpectedQueueRevision != state.Queue.QueueRevision)
            return Conflict("stale-playback-queue", state.Queue.QueueRevision);
        if (state.Queue.AutoSave != command.Enabled)
            state = state with { Revision = checked(state.Revision + 1), Queue = state.Queue with { AutoSave = command.Enabled } };
        return new Outcome<PlaybackState>.Success(state);
    }

    private async Task<Outcome<PlaybackState>> EditQueueAsync(EditQueue command, CommandContext context, CancellationToken token)
    {
        var intent = command.Edit switch
        {
            QueueEdit.Reorder { OrderedEntryIds.IsDefault: false } reorder =>
                HashIntent(new { Kind = nameof(QueueEdit.Reorder), command.ExpectedQueueRevision, command.ExpectedPlaylistRevision, Ids = reorder.OrderedEntryIds }),
            QueueEdit.Remove { EntryIds.IsDefault: false } remove =>
                HashIntent(new { Kind = nameof(QueueEdit.Remove), command.ExpectedQueueRevision, command.ExpectedPlaylistRevision, Ids = remove.EntryIds }),
            _ => null
        };
        if (intent is null)
            return Invalid("invalid-queue-entry-edit");
        var replay = await ReplayKnownQueueSaveAsync(context, intent, token).ConfigureAwait(false);
        if (replay is not null)
            return replay;
        if (command.ExpectedQueueRevision != state.Queue.QueueRevision)
            return Conflict("stale-playback-queue", state.Queue.QueueRevision);
        if (command.ExpectedPlaylistRevision != state.Queue.BackingPlaylist?.Revision)
            return Conflict("stale-saved-playlist", state.Queue.BackingPlaylist?.Revision);
        var edited = ApplyEdit(state.Queue.Entries, command.Edit);
        if (edited.IsDefault)
            return Invalid("invalid-queue-entry-edit");
        if (edited.SequenceEqual(state.Queue.Entries))
            return new Outcome<PlaybackState>.Success(state);
        var queue = state.Queue with { QueueRevision = checked(state.Queue.QueueRevision + 1), Entries = edited };
        return await CommitQueueEditAsync(WithQueue(queue), context, intent, token).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> CommitQueueEditAsync(PlaybackState next, CommandContext context, string intent, CancellationToken token)
    {
        if (playlistBinding is not { } binding || !next.Queue.AutoSave)
            return await CommitAsync(next with { Queue = next.Queue with { SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null, BatchSave = null } },
                false, token).ConfigureAwait(false);
        if (playlistPublication is null)
            return Invalid("queue-playlists-unavailable");
        var key = playlistPublication.Key(context);
        if (key.OperationId == Guid.Empty || key.OriginInstanceId == Guid.Empty)
            return Invalid("invalid-operation-id");

        var source = new QueuePlaylistSaveSource(next.SessionId, next.Queue.QueueRevision, intent, binding.LastSave, binding.LastSave is null);
        var operation = new PendingPlaylistSave(key, Guid.Empty, 0, binding.Version, binding.Name,
            [.. next.Queue.Entries.Select(entry => new PlaylistEntry(entry.EntryId, entry.TrackId))], PendingPlaylistSaveStatus.Pending, QueueSource: source);
        var result = await playlistPublication.StageQueueSaveAsync(MakeCheckpoint(next, binding), checkpointRevision, operation, context, token)
            .ConfigureAwait(false);
        if (result is not Outcome<PlaybackCheckpoint>.Success committed)
            return CopyFailure(result);
        checkpointRevision = committed.Value.CheckpointRevision;
        playlistBinding = committed.Value.PlaylistBacking;
        var batch = playlistBinding?.StagedSave;
        state = next with { Revision = checked(state.Revision + 1), Queue = next.Queue with
            { SaveStatus = batch is null ? QueueSaveStatus.Pending : QueueSaveStatus.StagedInBatch, SaveReceipt = null, BatchSave = batch } };
        return batch is null ? await PublishPendingQueueChangesAsync(context.Request, token).ConfigureAwait(false) : new Outcome<PlaybackState>.Success(state);
    }

    private async Task<Outcome<PlaybackState>?> ReplayKnownQueueSaveAsync(CommandContext context, string intent, CancellationToken token)
    {
        if (playlistPublication is null)
            return null;
        var key = playlistPublication.Key(context);
        var batched = await playlistPublication.ReadBatchSaveAsync(key, token).ConfigureAwait(false);
        if (batched is Outcome<LibraryBatchHandlers.StagedMutation?>.Conflict { Code: "library-batch-closed" })
        {
            var committed = await playlistPublication.ReadPublishedQueueSaveAsync(key, token).ConfigureAwait(false);
            if (committed is not Outcome<PublishedBatchQueueSave?>.Success { Value: var saved })
                return CopyFailure(committed);
            if (saved is not null)
                return saved.Source.SessionId == state.SessionId && saved.Source.IntentHash == intent
                    ? new Outcome<PlaybackState>.Success(state) : Conflict("operation-intent-mismatch", null);
        }
        if (batched is not Outcome<LibraryBatchHandlers.StagedMutation?>.Success { Value: var staged })
            return CopyFailure(batched);
        if (staged is not null)
            return staged.Change is LibraryBatchChange.Playlist { QueueSource: { } stagedSource } &&
                stagedSource.SessionId == state.SessionId && stagedSource.IntentHash == intent
                ? new Outcome<PlaybackState>.Success(state) : Conflict("operation-intent-mismatch", null);
        var known = await playlistPublication.Store.ReadQueueSaveAsync(key, token).ConfigureAwait(false);
        if (known is not Outcome<PendingPlaylistSave?>.Success read)
            return CopyFailure(known);
        if (read.Value is null)
            return null;
        return read.Value.QueueSource is { } source && source.SessionId == state.SessionId && source.IntentHash == intent
            ? await PublishPendingQueueChangesAsync(context.Request, token).ConfigureAwait(false)
            : Conflict("operation-intent-mismatch", null);
    }

    private async Task<Outcome<PlaybackState>> PublishPendingQueueChangesAsync(RequestContext context, CancellationToken token)
    {
        if (playlistPublication is not null)
        {
            await playlistPublication.ReplayAsync(state.SessionId, context, token).ConfigureAwait(false);
            await RefreshSaveStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return new Outcome<PlaybackState>.Success(state);
    }

    private async Task RefreshSaveStatusAsync(CancellationToken token)
    {
        if (playlistPublication is not null && playlistBinding is { StagedSave: { } staged } active &&
            await RefreshBatchSaveStatusAsync(active, staged, token).ConfigureAwait(false))
            return;
        if (playlistPublication is null || playlistBinding is not { LastSave: { } key } binding)
            return;
        var result = await playlistPublication.Store.ReadQueueSaveAsync(key, token).ConfigureAwait(false);
        if (result is not Outcome<PendingPlaylistSave?>.Success { Value: { QueueSource: { } source } operation })
            return;
        var committedRevision = operation.Receipt?.Revision ??
            (source.ExpectedRevisionPinned ? operation.BasePlaylist?.Revision : null);
        if (committedRevision > binding.Version.Revision)
            playlistBinding = binding = binding with { Version = new(binding.Version.PlaylistId, committedRevision.Value) };
        var queue = state.Queue with { BackingPlaylist = binding.Version };
        if (queue.QueueRevision == source.QueueRevision)
        {
            var status = operation.Status switch
            {
                PendingPlaylistSaveStatus.Accepted => QueueSaveStatus.Saved,
                PendingPlaylistSaveStatus.Conflicted => QueueSaveStatus.Conflict,
                PendingPlaylistSaveStatus.Rejected => QueueSaveStatus.Failed,
                _ => QueueSaveStatus.Pending
            };
            queue = queue with { SaveStatus = status, SaveReceipt = operation.Receipt, BatchSave = null };
        }
        if (queue != state.Queue)
            state = state with { Revision = checked(state.Revision + 1), Queue = queue };
    }

    private async Task<bool> RefreshBatchSaveStatusAsync(PlaylistQueueBinding binding, LibraryBatchStageReceipt receipt, CancellationToken token)
    {
        var current = await playlistPublication!.ReadBatchSaveAsync(receipt.OperationId, token).ConfigureAwait(false);
        if (current is Outcome<LibraryBatchHandlers.StagedMutation?>.Success
            { Value: { Change: LibraryBatchChange.Playlist { QueueSource: { } source } } } &&
            source.SessionId == state.SessionId && source.QueueRevision <= state.Queue.QueueRevision)
        {
            if (source.QueueRevision == state.Queue.QueueRevision)
                ApplyQueueSaveStatus(QueueSaveStatus.StagedInBatch, receipt);
            return true;
        }
        if (current is Outcome<LibraryBatchHandlers.StagedMutation?>.Conflict { Code: "library-batch-closed" })
        {
            var result = await playlistPublication.ReadPublishedQueueSaveAsync(receipt.OperationId, token).ConfigureAwait(false);
            if (result is not Outcome<PublishedBatchQueueSave?>.Success { Value: var committed })
                return true;
            if (committed is not null && ApplyPublishedBatchQueueSave(binding, committed))
                return true;
        }
        if (current is Outcome<LibraryBatchHandlers.StagedMutation?>.Success or Outcome<LibraryBatchHandlers.StagedMutation?>.Conflict { Code: "library-batch-closed" })
        {
            playlistBinding = binding with { StagedSave = null };
            ApplyQueueSaveStatus(QueueSaveStatus.NotRequested, null);
            return false;
        }
        return true;
    }

    /// <summary>Rebinds the queue while preserving playback order and the operation link across restart.</summary>
    /// <remarks>2026-11-24: Adopt the authoritative playlist version for FR-LIBRARY-002#ac-batch-commit and FR-PLAYER-001#ac-queue-autosave.</remarks>
    private bool ApplyPublishedBatchQueueSave(PlaylistQueueBinding binding, PublishedBatchQueueSave committed)
    {
        if (committed.Source.SessionId != state.SessionId || committed.Source.QueueRevision > state.Queue.QueueRevision ||
            committed.Version.PlaylistId != binding.Version.PlaylistId || committed.Version.Revision < binding.Version.Revision)
            return false;

        playlistBinding = binding with { Version = committed.Version, LastSave = null };
        var queue = state.Queue with { BackingPlaylist = committed.Version, BatchSave = null };
        if (queue.QueueRevision == committed.Source.QueueRevision)
        {
            queue = queue with { SaveStatus = QueueSaveStatus.Saved,
                SaveReceipt = new(Guid.Empty, committed.Version.PlaylistId, committed.Version.Revision, QueueSessionId: state.SessionId) };
        }
        if (queue != state.Queue)
            state = state with { Revision = checked(state.Revision + 1), Queue = queue };
        return true;
    }

    private void ApplyQueueSaveStatus(QueueSaveStatus status, LibraryBatchStageReceipt? receipt)
    {
        var queue = state.Queue with { SaveStatus = status, SaveReceipt = null, BatchSave = receipt };
        if (queue != state.Queue)
            state = state with { Revision = checked(state.Revision + 1), Queue = queue };
    }

    private PlaybackState WithQueue(QueueSnapshot queue) => state with
    {
        Queue = queue,
        ActiveQueueEntryId = queue.Entries.Any(entry => entry.EntryId == state.ActiveQueueEntryId && entry.TrackId == state.ActiveTrackId)
            ? state.ActiveQueueEntryId : null
    };

    private PlaybackCheckpoint MakeCheckpoint(PlaybackState next, PlaylistQueueBinding? binding) =>
        new(next.SessionId, checked(checkpointRevision + 1), next.Queue.QueueRevision, next.Queue.Entries, next.ActiveQueueEntryId,
            next.ActiveQueueEntryId.HasValue ? next.Position : TimeSpan.Zero, next.Queue.Shuffle, next.Queue.Repeat, binding, ActivePlaybackOccurrence(next),
            next.ActiveQueueEntryId.HasValue && next.Status == TransportStatus.Playing);

    private static string HashIntent<T>(T intent) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent)));

    private static Outcome<PlaybackState> Conflict(string code, long? actualRevision) => new Outcome<PlaybackState>.Conflict(code, actualRevision);

    private static ImmutableArray<QueueEntry> ApplyEdit(ImmutableArray<QueueEntry> entries, QueueEdit edit)
    {
        var ids = edit is QueueEdit.Reorder reorder ? reorder.OrderedEntryIds : ((QueueEdit.Remove)edit).EntryIds;
        var entriesById = entries.ToDictionary(entry => entry.EntryId);
        var chosen = ids.ToHashSet();
        if (chosen.Count != ids.Length || ids.Any(id => !entriesById.ContainsKey(id)))
            return default;
        if (edit is QueueEdit.Reorder)
            return ids.Length == entries.Length ? [.. ids.Select(id => entriesById[id])] : default;
        return ids.Length == 0 ? default : [.. entries.Where(entry => !chosen.Contains(entry.EntryId))];
    }
}
