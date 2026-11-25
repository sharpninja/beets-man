using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Loads and publishes queue-backed playlists through the same owner used for ordinary playlist saves.</summary>
/// <remarks>2026-11-24: Replay exact ordered queue edits for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed class QueuePlaylistPublication(IPublishedPlaylistReader catalog, IPlaylistPublicationHost host, IQueuePlaylistSaveStore store,
    Guid localOrigin)
{
    internal IQueuePlaylistSaveStore Store => store;

    internal Task<Outcome<PublishedPlaylist>> ReadPlaylistAsync(Guid id, CancellationToken token) => catalog.ReadAsync(id, token);

    internal OperationKey Key(CommandContext context) => new(context.Request.OriginInstanceId ?? localOrigin, context.OperationId);

    internal async Task<Outcome<bool>> ReplayAsync(Guid sessionId, RequestContext context, CancellationToken token)
    {
        var pending = await store.ReadPendingQueueSavesAsync(sessionId, token).ConfigureAwait(false);
        if (pending is not Outcome<ImmutableArray<PendingPlaylistSave>>.Success loaded)
            return pending is Outcome<ImmutableArray<PendingPlaylistSave>>.Cancelled
                ? new Outcome<bool>.Cancelled() : new Outcome<bool>.DependencyFailure("queue-saves-unavailable", true);
        var blocked = new HashSet<Guid>();
        foreach (var operation in loaded.Value)
        {
            if (token.IsCancellationRequested)
                return new Outcome<bool>.Cancelled();
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
