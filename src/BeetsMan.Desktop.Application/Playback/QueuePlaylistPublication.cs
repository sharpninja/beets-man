using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Loads and publishes queue-backed playlists through the same owner used for ordinary playlist saves.</summary>
/// <remarks>2026-11-24: Replay exact ordered queue edits for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
/// <remarks>2026-11-24: Use the batch mode gate for auto-save and background replay under FR-LIBRARY-002#ac-batch-local.</remarks>
public sealed class QueuePlaylistPublication(IPublishedPlaylistReader catalog, IPlaylistPublicationHost host, IQueuePlaylistSaveStore store,
    Guid localOrigin, LibraryBatchHandlers? batches = null)
{
    private readonly SemaphoreSlim standaloneReplay = new(1, 1);

    internal IQueuePlaylistSaveStore Store => store;

    internal Task<Outcome<PublishedPlaylist>> ReadPlaylistAsync(Guid id, CancellationToken token) => catalog.ReadAsync(id, token);

    internal OperationKey Key(CommandContext context) => new(context.Request.OriginInstanceId ?? localOrigin, context.OperationId);

    internal Task<Outcome<PlaybackCheckpoint>> StageQueueSaveAsync(PlaybackCheckpoint checkpoint, long expectedRevision,
        PendingPlaylistSave operation, CommandContext context, CancellationToken token)
    {
        var binding = checkpoint.PlaylistBacking ?? throw new InvalidOperationException("The queue save requires a playlist binding.");
        var direct = checkpoint with { PlaylistBacking = binding with { LastSave = operation.Key, StagedSave = null } };
        Task<Outcome<PlaybackCheckpoint>> StageDirectAsync(CancellationToken ct) => store.StageQueueSaveAsync(direct, expectedRevision, operation, ct);
        if (batches is null)
            return StageDirectAsync(token);

        var change = new LibraryBatchChange.Playlist(Guid.Empty, 0, operation.BasePlaylist, operation.Name, operation.Entries,
            operation.QueueSource! with { Predecessor = null, ExpectedRevisionPinned = true });
        var scoped = context with { Request = context.Request with { OriginInstanceId = operation.Key.OriginInstanceId } };
        return batches.RouteMutationAsync<LibraryBatchChange.Playlist, PlaybackCheckpoint>(scoped, ct => WithCurrentBaseAsync(StageDirectAsync, ct),
            ct => WithCurrentBaseAsync(_ => Task.FromResult<Outcome<LibraryBatchChange.Playlist>>(new Outcome<LibraryBatchChange.Playlist>.Success(change)), ct),
            pending => pending.QueueSource == change.QueueSource && pending.BasePlaylist == change.BasePlaylist && pending.Entries.SequenceEqual(change.Entries),
            (_, receipt) => checkpoint with { PlaylistBacking = binding with { StagedSave = receipt } }, token,
            (stage, ct) => store is IQueuePlaylistBatchStore batchStore ? batchStore.StageQueueBatchSaveAsync(checkpoint, expectedRevision, stage, ct) :
                Task.FromResult<Outcome<LibraryBatchStageReceipt>>(new Outcome<LibraryBatchStageReceipt>.DependencyFailure("queue-batch-store-unavailable", false)));

        // The route's mutation lane also protects batch finalization. Do not stage an edit using a base that changed after playback read it.
        async Task<Outcome<T>> WithCurrentBaseAsync<T>(Func<CancellationToken, Task<Outcome<T>>> stage, CancellationToken ct)
        {
            if (binding.StagedSave is { } prior)
            {
                var result = await ReadPublishedQueueSaveAsync(new OperationKey(operation.Key.OriginInstanceId, prior.OperationId), ct).ConfigureAwait(false);
                if (result is not Outcome<PublishedBatchQueueSave?>.Success { Value: var published })
                    return LibraryBatchHandlers.CopyFailure<PublishedBatchQueueSave?, T>(result);
                if (published is not null && published.Version.PlaylistId == binding.Version.PlaylistId && published.Version.Revision > binding.Version.Revision)
                    return new Outcome<T>.Conflict("stale-saved-playlist", published.Version.Revision);
            }
            return await stage(ct).ConfigureAwait(false);
        }
    }

    internal Task<Outcome<LibraryBatchHandlers.StagedMutation?>> ReadBatchSaveAsync(OperationKey key, CancellationToken token) =>
        batches?.ReadKnownStageAsync(new(new(Guid.NewGuid(), key.OriginInstanceId), key.OperationId), token) ??
        Task.FromResult<Outcome<LibraryBatchHandlers.StagedMutation?>>(new Outcome<LibraryBatchHandlers.StagedMutation?>.Success(null));

    internal Task<Outcome<LibraryBatchHandlers.StagedMutation?>> ReadBatchSaveAsync(Guid operationId, CancellationToken token) =>
        ReadBatchSaveAsync(new OperationKey(localOrigin, operationId), token);

    /// <summary>Resolves the authoritative result of a batch queue edit for FR-PLAYER-001#ac-queue-autosave.</summary>
    internal Task<Outcome<PublishedBatchQueueSave?>> ReadPublishedQueueSaveAsync(OperationKey key, CancellationToken token) =>
        batches?.ReadPublishedQueueSaveAsync(key, token) ?? Task.FromResult<Outcome<PublishedBatchQueueSave?>>(new Outcome<PublishedBatchQueueSave?>.Success(null));

    /// <summary>Resolves a batch save by the local playback session's origin.</summary>
    internal Task<Outcome<PublishedBatchQueueSave?>> ReadPublishedQueueSaveAsync(Guid operationId, CancellationToken token) =>
        ReadPublishedQueueSaveAsync(new OperationKey(localOrigin, operationId), token);

    internal Task<Outcome<bool>> ReplayAsync(Guid sessionId, RequestContext context, CancellationToken token) => batches is null
        ? ReplayWithoutBatchAsync(sessionId, context, token)
        : batches.ReplayDirectAsync(context with { OriginInstanceId = context.OriginInstanceId ?? localOrigin },
            ct => ReplayDirectAsync(sessionId, context, ct), token);

    /// <summary>Serializes direct replay when the workspace has no batch mutation gate.</summary>
    /// <remarks>2026-11-25: Keep the pinned predecessor order independent of the playback lane for FR-PLAYER-001#ac-queue-autosave.</remarks>
    private async Task<Outcome<bool>> ReplayWithoutBatchAsync(Guid sessionId, RequestContext context, CancellationToken token)
    {
        await standaloneReplay.WaitAsync(token).ConfigureAwait(false);
        try { return await ReplayDirectAsync(sessionId, context, token).ConfigureAwait(false); }
        finally { standaloneReplay.Release(); }
    }

    private async Task<Outcome<bool>> ReplayDirectAsync(Guid sessionId, RequestContext context, CancellationToken token)
    {
        var pending = await store.ReadPendingQueueSavesAsync(sessionId, token).ConfigureAwait(false);
        if (pending is not Outcome<ImmutableArray<PendingPlaylistSave>>.Success loaded)
            return pending is Outcome<ImmutableArray<PendingPlaylistSave>>.Cancelled
                ? new Outcome<bool>.Cancelled() : new Outcome<bool>.DependencyFailure("queue-saves-unavailable", true);
        var blocked = new HashSet<Guid>();
        // 2026-11-25: A resumed session publishes only its registered origin's intents for FR-RECORDS-001#ac-offline.
        var origin = context.OriginInstanceId ?? localOrigin;
        foreach (var operation in loaded.Value)
        {
            if (token.IsCancellationRequested)
                return new Outcome<bool>.Cancelled();
            if (operation.Key.OriginInstanceId != origin)
                continue;
            var playlistId = operation.BasePlaylist!.PlaylistId;
            if (!blocked.Contains(playlistId) && !await TryPublishAsync(operation, context, token).ConfigureAwait(false))
                blocked.Add(playlistId);
        }
        return new Outcome<bool>.Success(blocked.Count == 0);
    }

    private async Task<bool> TryPublishAsync(PendingPlaylistSave operation, RequestContext context, CancellationToken token)
    {
        var source = operation.QueueSource!;
        if (!source.ExpectedRevisionPinned)
        {
            if (source.Predecessor is not { } predecessor)
                return false;
            var previous = await store.ReadQueueSaveAsync(predecessor, token).ConfigureAwait(false);
            if (previous is not Outcome<PendingPlaylistSave?>.Success { Value: { } prior } ||
                prior.QueueSource?.SessionId != source.SessionId || prior.BasePlaylist?.PlaylistId != operation.BasePlaylist?.PlaylistId)
                return false;
            if (prior.Status == PendingPlaylistSaveStatus.Pending)
                return false;
            if (prior.Receipt is not { } receipt)
                return await RecordAsync(operation with { Status = PendingPlaylistSaveStatus.Conflicted,
                    ConflictCode = "queue-save-predecessor-unresolved", ActualHostRevision = prior.ActualHostRevision }).ConfigureAwait(false);

            operation = operation with { BasePlaylist = new(receipt.PlaylistId, receipt.Revision),
                QueueSource = source with { ExpectedRevisionPinned = true } };
            var pinned = await store.RecordQueueSaveAsync(operation, token).ConfigureAwait(false);
            if (pinned is not Outcome<PendingPlaylistSave>.Success persisted)
                return false;
            operation = persisted.Value;
        }

        var request = context with { OriginInstanceId = operation.Key.OriginInstanceId };
        Outcome<PlaylistSavedReceipt> published;
        try { published = await host.SaveAsync(operation, request, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        var backing = operation.BasePlaylist!;
        return published switch
        {
            Outcome<PlaylistSavedReceipt>.Success { Value: var saved } when saved.QueueSessionId == source.SessionId &&
                saved.DraftId == Guid.Empty && saved.PlaylistId == backing.PlaylistId && saved.Revision > backing.Revision =>
                await RecordAsync(operation with { Status = PendingPlaylistSaveStatus.Accepted, Receipt = saved }).ConfigureAwait(false),
            Outcome<PlaylistSavedReceipt>.Conflict conflict =>
                await RecordAsync(operation with { Status = PendingPlaylistSaveStatus.Conflicted,
                    ConflictCode = conflict.Code, ActualHostRevision = conflict.ActualRevision }).ConfigureAwait(false),
            Outcome<PlaylistSavedReceipt>.Invalid invalid =>
                await RecordAsync(operation with { Status = PendingPlaylistSaveStatus.Rejected, Issues = [.. invalid.Issues] }).ConfigureAwait(false),
            _ => false
        };
    }

    private async Task<bool> RecordAsync(PendingPlaylistSave operation) =>
        await store.RecordQueueSaveAsync(operation, CancellationToken.None).ConfigureAwait(false) is Outcome<PendingPlaylistSave>.Success;
}
