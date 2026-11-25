using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Coordinates playlist backing, ordered queue edits, and durable auto-save receipts.</summary>
/// <remarks>2026-11-24: Implement FR-PLAYER-001#ac-queue-autosave and preserve retries under FR-RECORDS-001#ac-offline.</remarks>
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
    public Task<Outcome<PlaybackState>> ReplayQueuePlaylistSavesAsync(Guid sessionId, RequestContext context, CancellationToken token) =>
        RunAsync(sessionId, ct => PublishPendingQueueChangesAsync(context, ct), token);

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
            if (save.Value?.Status == PendingPlaylistSaveStatus.Pending)
                binding = binding with { LastSave = key };
        }
        var queue = state.Queue with
        {
            QueueRevision = checked(state.Queue.QueueRevision + 1), BackingPlaylist = playlist.Version,
            Entries = [.. playlist.Entries.Select(entry => new QueueEntry(entry.EntryId, entry.TrackId))],
            AutoSave = playlistBinding?.Version.PlaylistId == playlist.Version.PlaylistId ? state.Queue.AutoSave : initialAutoSave,
            SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null
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
            return await CommitAsync(next with { Queue = next.Queue with { SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null } },
                false, token).ConfigureAwait(false);
        if (playlistPublication is null)
            return Invalid("queue-playlists-unavailable");
        var key = playlistPublication.Key(context);
        if (key.OperationId == Guid.Empty || key.OriginInstanceId == Guid.Empty)
            return Invalid("invalid-operation-id");

        var source = new QueuePlaylistSaveSource(next.SessionId, next.Queue.QueueRevision, intent, binding.LastSave, binding.LastSave is null);
        var operation = new PendingPlaylistSave(key, Guid.Empty, 0, binding.Version, binding.Name,
            [.. next.Queue.Entries.Select(entry => new PlaylistEntry(entry.EntryId, entry.TrackId))], PendingPlaylistSaveStatus.Pending, QueueSource: source);
        var targeted = binding with { LastSave = key };
        var result = await playlistPublication.Store.StageQueueSaveAsync(MakeCheckpoint(next, targeted), checkpointRevision, operation, token)
            .ConfigureAwait(false);
        if (result is not Outcome<PlaybackCheckpoint>.Success committed)
            return CopyFailure(result);
        checkpointRevision = committed.Value.CheckpointRevision;
        playlistBinding = targeted;
        state = next with { Revision = checked(state.Revision + 1), Queue = next.Queue with { SaveStatus = QueueSaveStatus.Pending, SaveReceipt = null } };
        return await PublishPendingQueueChangesAsync(context.Request, token).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>?> ReplayKnownQueueSaveAsync(CommandContext context, string intent, CancellationToken token)
    {
        if (playlistPublication is null)
            return null;
        var known = await playlistPublication.Store.ReadQueueSaveAsync(playlistPublication.Key(context), token).ConfigureAwait(false);
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
            queue = queue with { SaveStatus = status, SaveReceipt = operation.Receipt };
        }
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
            next.ActiveQueueEntryId.HasValue ? next.Position : TimeSpan.Zero, next.Queue.Shuffle, next.Queue.Repeat, binding);

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
