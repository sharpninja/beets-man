using System.Collections.Concurrent;
using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Persistence;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Contains one published playlist available through the cached catalog.</summary>
/// <remarks>2026-11-24: Define the source snapshot for FR-LIBRARY-001#ac-browse and FR-RECORDS-001#ac-offline.</remarks>
public sealed record PublishedPlaylist(PlaylistVersion Version, string Name, ImmutableArray<PlaylistEntry> Entries);

/// <summary>Reads saved playlist content from the current catalog projection.</summary>
public interface IPublishedPlaylistReader
{
    /// <summary>Returns the authoritative cached playlist or a typed read failure.</summary>
    Task<Outcome<PublishedPlaylist>> ReadAsync(Guid playlistId, CancellationToken cancellationToken);
}

/// <summary>Publishes a durable pending save through the authenticated host boundary.</summary>
public interface IPlaylistPublicationHost
{
    /// <summary>Uses the supplied operation key to return the original receipt on replay.</summary>
    Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken cancellationToken);
}

/// <summary>Coordinates local playlist drafts and replayable, revision-checked host saves.</summary>
/// <remarks>2026-11-24: Bind the Library and playlist workflow and preserve pending saves under FR-RECORDS-001#ac-offline.</remarks>
/// <remarks>2026-11-24: Route named playlist saves through FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed class PlaylistWorkspaceHandlers(IPlaylistDraftReader reader, IWriteUnitOfWork<IPlaylistDraftWriteRepositories> writes,
    IPublishedPlaylistReader catalog, IPlaylistPublicationHost host, Guid localOrigin, LibraryBatchHandlers? batches = null) :
    IQueryHandler<GetPlaylistDraft, PlaylistDraft>, ICommandHandler<CreatePlaylistDraft, PlaylistDraft>,
    ICommandHandler<EditPlaylistDraft, PlaylistDraft>, ICommandHandler<SavePlaylist, PlaylistSavedReceipt>
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> publishingByDraft = new();

    /// <summary>Registers the query and all mutations for the playlist workspace.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetPlaylistDraft, PlaylistDraft>(this).RegisterCommand<CreatePlaylistDraft, PlaylistDraft>(this)
        .RegisterCommand<EditPlaylistDraft, PlaylistDraft>(this).RegisterCommand<SavePlaylist, PlaylistSavedReceipt>(this);

    /// <inheritdoc />
    public async Task<Outcome<PlaylistDraft>> HandleAsync(GetPlaylistDraft query, RequestContext context, CancellationToken token)
    {
        var result = await reader.ReadDraftAsync(query.DraftId, token).ConfigureAwait(false);
        return result is Outcome<PlaylistDraft?>.Success found
            ? found.Value is { } draft ? new Outcome<PlaylistDraft>.Success(draft) : Invalid<PlaylistDraft>("playlist-draft-not-found")
            : CopyFailure<PlaylistDraft?, PlaylistDraft>(result);
    }

    /// <inheritdoc />
    public async Task<Outcome<PlaylistDraft>> HandleAsync(CreatePlaylistDraft command, CommandContext context, CancellationToken token)
    {
        if (context.OperationId == Guid.Empty)
            return Invalid<PlaylistDraft>("invalid-operation-id");
        PlaylistDraft proposed;
        switch (command.Source)
        {
            case PlaylistDraftSource.Empty:
                proposed = new(context.OperationId, 1, "", [], null);
                break;
            case PlaylistDraftSource.QueueSnapshot snapshot when snapshot.SessionId != Guid.Empty && !snapshot.OrderedTrackIds.IsDefault &&
                snapshot.OrderedTrackIds.All(id => id != Guid.Empty):
                proposed = new(context.OperationId, 1, "", [.. snapshot.OrderedTrackIds.Select(id => new PlaylistEntry(Guid.NewGuid(), id))], null);
                break;
            case PlaylistDraftSource.SavedPlaylist saved:
                var lookup = await catalog.ReadAsync(saved.PlaylistId, token).ConfigureAwait(false);
                if (lookup is not Outcome<PublishedPlaylist>.Success source)
                    return CopyFailure<PublishedPlaylist, PlaylistDraft>(lookup);
                if (source.Value.Version != new PlaylistVersion(saved.PlaylistId, saved.ExpectedRevision))
                    return new Outcome<PlaylistDraft>.Conflict("stale-saved-playlist", source.Value.Version.Revision);
                proposed = new(context.OperationId, 1, source.Value.Name, source.Value.Entries, source.Value.Version);
                break;
            default:
                return Invalid<PlaylistDraft>("invalid-playlist-source");
        }

        return await writes.ExecuteAsync<PlaylistDraft>(repositories =>
        {
            if (repositories.Drafts.Find(proposed.DraftId) is { } existing)
            {
                if (existing.Revision == proposed.Revision && existing.BasePlaylist == proposed.BasePlaylist &&
                    existing.Name == proposed.Name && existing.Entries.Select(entry => entry.TrackId).SequenceEqual(proposed.Entries.Select(entry => entry.TrackId)))
                    return new Outcome<PlaylistDraft>.Success(existing);
                return new Outcome<PlaylistDraft>.Conflict("playlist-draft-already-created", existing.Revision);
            }
            repositories.Drafts.Add(proposed);
            return new Outcome<PlaylistDraft>.Success(proposed);
        }, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Outcome<PlaylistDraft>> HandleAsync(EditPlaylistDraft command, CommandContext context, CancellationToken token) =>
        writes.ExecuteAsync<PlaylistDraft>(repositories =>
        {
            if (repositories.Drafts.Find(command.DraftId) is not { } current)
                return Invalid<PlaylistDraft>("playlist-draft-not-found");
            if (current.Revision != command.ExpectedRevision)
                return new Outcome<PlaylistDraft>.Conflict("stale-playlist-draft", current.Revision);
            var edited = ApplyEdit(current.Entries, command.Operation);
            if (edited.IsDefault)
                return Invalid<PlaylistDraft>("invalid-playlist-entry-edit");
            var result = current with { Revision = checked(current.Revision + 1), Entries = edited };
            return repositories.Drafts.TryReplace(result, current.Revision)
                ? new Outcome<PlaylistDraft>.Success(result) : new Outcome<PlaylistDraft>.Conflict("stale-playlist-draft", null);
        }, token);

    /// <inheritdoc />
    public async Task<Outcome<PlaylistSavedReceipt>> HandleAsync(SavePlaylist command, CommandContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            return Invalid<PlaylistSavedReceipt>("required-playlist-name", nameof(command.Name));
        var identity = new OperationKey(context.Request.OriginInstanceId ?? localOrigin, context.OperationId);
        if (identity.OperationId == Guid.Empty || identity.OriginInstanceId == Guid.Empty)
            return Invalid<PlaylistSavedReceipt>("invalid-operation-id");

        var lane = publishingByDraft.GetOrAdd(command.DraftId, static _ => new SemaphoreSlim(1, 1));
        try { await lane.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PlaylistSavedReceipt>.Cancelled(); }
        try
        {
            if (batches is null)
                return await StageAndPublishAsync(command, context.Request, identity, token).ConfigureAwait(false);
            var scoped = new CommandContext(context.Request with { OriginInstanceId = identity.OriginInstanceId }, identity.OperationId);
            return await batches.RouteMutationAsync<LibraryBatchChange.Playlist, PlaylistSavedReceipt>(scoped,
                ct => StageAndPublishAsync(command, context.Request, identity, ct), ct => PrepareBatchSaveAsync(command, ct),
                pending => pending.QueueSource is null && pending.DraftId == command.DraftId && pending.DraftRevision == command.ExpectedDraftRevision &&
                    pending.BasePlaylist?.Revision == command.ExpectedPlaylistRevision && pending.Name == command.Name,
                (pending, receipt) => new(pending.DraftId, pending.BasePlaylist?.PlaylistId ?? Guid.Empty, pending.BasePlaylist?.Revision ?? 0,
                    pending.DraftRevision, LocalBatch: receipt), token).ConfigureAwait(false);
        }
        finally { lane.Release(); }
    }

    private async Task<Outcome<LibraryBatchChange.Playlist>> PrepareBatchSaveAsync(SavePlaylist command, CancellationToken token)
    {
        var read = await reader.ReadDraftAsync(command.DraftId, token).ConfigureAwait(false);
        if (read is not Outcome<PlaylistDraft?>.Success { Value: var draft })
            return CopyFailure<PlaylistDraft?, LibraryBatchChange.Playlist>(read);
        if (draft is null)
            return Invalid<LibraryBatchChange.Playlist>("playlist-draft-not-found");
        if (draft.Revision != command.ExpectedDraftRevision)
            return new Outcome<LibraryBatchChange.Playlist>.Conflict("stale-playlist-draft", draft.Revision);
        if (draft.BasePlaylist?.Revision != command.ExpectedPlaylistRevision)
            return new Outcome<LibraryBatchChange.Playlist>.Conflict("stale-saved-playlist", draft.BasePlaylist?.Revision);
        return new Outcome<LibraryBatchChange.Playlist>.Success(new(draft.DraftId, draft.Revision, draft.BasePlaylist, command.Name, draft.Entries));
    }

    private async Task<Outcome<PlaylistSavedReceipt>> StageAndPublishAsync(SavePlaylist command, RequestContext request,
        OperationKey key, CancellationToken token)
    {
        var staged = await writes.ExecuteAsync(repositories => Stage(repositories, command, key), token).ConfigureAwait(false);
        if (staged is not Outcome<PendingPlaylistSave>.Success success)
            return CopyFailure<PendingPlaylistSave, PlaylistSavedReceipt>(staged);

        return await PublishPendingAsync(success.Value, request, token).ConfigureAwait(false);
    }

    /// <summary>Replays durable named saves for the registered origin after connectivity is restored.</summary>
    /// <remarks>2026-11-25: Reuse the pinned payload and defer direct sends during FR-LIBRARY-002#ac-batch-local for FR-RECORDS-001#ac-offline.</remarks>
    public async Task ReplayPendingSavesAsync(RequestContext request, CancellationToken token)
    {
        if (request.OriginInstanceId != localOrigin || localOrigin == Guid.Empty)
            return;
        var pending = await reader.ReadPendingDraftSavesAsync(localOrigin, token).ConfigureAwait(false);
        if (pending is not Outcome<ImmutableArray<PendingPlaylistSave>>.Success { Value: var saves })
            return;

        foreach (var draft in saves.GroupBy(save => save.DraftId))
        {
            var lane = publishingByDraft.GetOrAdd(draft.Key, static _ => new SemaphoreSlim(1, 1));
            await lane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                foreach (var save in draft)
                {
                    token.ThrowIfCancellationRequested();
                    var replayed = batches is null
                        ? await ReplayAsync(save.Key, request, token).ConfigureAwait(false)
                        : await batches.ReplayDirectAsync(request, ct => ReplayAsync(save.Key, request, ct), token).ConfigureAwait(false);
                    if (replayed is Outcome<bool>.Success { Value: false })
                        return;
                    if (replayed is not Outcome<bool>.Success)
                        break;
                }
            }
            finally { lane.Release(); }
        }
    }

    private async Task<Outcome<bool>> ReplayAsync(OperationKey key, RequestContext request, CancellationToken token)
    {
        var current = await writes.ExecuteAsync<PendingPlaylistSave?>(
            repositories => new Outcome<PendingPlaylistSave?>.Success(repositories.Operations.Find(key)), token).ConfigureAwait(false);
        if (current is not Outcome<PendingPlaylistSave?>.Success available)
            return CopyFailure<PendingPlaylistSave?, bool>(current);
        if (available.Value is not { QueueSource: null, Status: PendingPlaylistSaveStatus.Pending } pending)
            return new Outcome<bool>.Success(true);

        var result = await PublishPendingAsync(pending, request, token).ConfigureAwait(false);
        return result is Outcome<PlaylistSavedReceipt>.Success or Outcome<PlaylistSavedReceipt>.Conflict or Outcome<PlaylistSavedReceipt>.Invalid
            ? new Outcome<bool>.Success(true)
            : CopyFailure<PlaylistSavedReceipt, bool>(result);
    }

    private async Task<Outcome<PlaylistSavedReceipt>> PublishPendingAsync(PendingPlaylistSave pending, RequestContext request, CancellationToken token)
    {
        if (pending.Receipt is { } committed)
            return new Outcome<PlaylistSavedReceipt>.Success(committed);
        if (pending.Status == PendingPlaylistSaveStatus.Conflicted)
            return new Outcome<PlaylistSavedReceipt>.Conflict(pending.ConflictCode ?? "stale-saved-playlist", pending.ActualHostRevision);
        if (pending.Status == PendingPlaylistSaveStatus.Rejected)
            return new Outcome<PlaylistSavedReceipt>.Invalid(pending.Issues);

        var result = await host.SaveAsync(pending, request, token).ConfigureAwait(false);
        switch (result)
        {
            case Outcome<PlaylistSavedReceipt>.Success saved:
                var receipt = saved.Value;
                if (receipt.DraftId != pending.DraftId || receipt.PlaylistId == Guid.Empty ||
                    pending.BasePlaylist is { } backing && (backing.PlaylistId != receipt.PlaylistId || receipt.Revision <= backing.Revision))
                    return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-host-receipt-mismatch", true);
                var local = await writes.ExecuteAsync(repositories => Complete(repositories, pending, receipt), CancellationToken.None).ConfigureAwait(false);
                return local is Outcome<PlaylistSavedReceipt>.Success ? local : saved;
            case Outcome<PlaylistSavedReceipt>.Conflict conflict:
                await RecordNonSuccessAsync(pending with { Status = PendingPlaylistSaveStatus.Conflicted, ConflictCode = conflict.Code,
                    ActualHostRevision = conflict.ActualRevision }).ConfigureAwait(false);
                return conflict;
            case Outcome<PlaylistSavedReceipt>.Invalid invalid:
                await RecordNonSuccessAsync(pending with { Status = PendingPlaylistSaveStatus.Rejected, Issues = [.. invalid.Issues] }).ConfigureAwait(false);
                return invalid;
            default:
                return result;
        }
    }

    private Task<Outcome<bool>> RecordNonSuccessAsync(PendingPlaylistSave operation) => writes.ExecuteAsync<bool>(repositories =>
        new Outcome<bool>.Success(repositories.Operations.TryReplace(operation, PendingPlaylistSaveStatus.Pending)), CancellationToken.None);

    private static Outcome<PendingPlaylistSave> Stage(IPlaylistDraftWriteRepositories repositories, SavePlaylist command, OperationKey key)
    {
        if (repositories.Operations.Find(key) is { } matching)
            return matching.QueueSource is null && matching.DraftId == command.DraftId && matching.DraftRevision == command.ExpectedDraftRevision &&
                matching.BasePlaylist?.Revision == command.ExpectedPlaylistRevision && matching.Name == command.Name
                ? new Outcome<PendingPlaylistSave>.Success(matching)
                : new Outcome<PendingPlaylistSave>.Conflict("operation-intent-mismatch", null);

        if (repositories.Drafts.Find(command.DraftId) is not { } draft)
            return Invalid<PendingPlaylistSave>("playlist-draft-not-found");
        if (draft.Revision != command.ExpectedDraftRevision)
            return new Outcome<PendingPlaylistSave>.Conflict("stale-playlist-draft", draft.Revision);
        if (draft.BasePlaylist?.Revision != command.ExpectedPlaylistRevision)
            return new Outcome<PendingPlaylistSave>.Conflict("stale-saved-playlist", draft.BasePlaylist?.Revision);

        var pending = new PendingPlaylistSave(key, draft.DraftId, draft.Revision, draft.BasePlaylist, command.Name, draft.Entries,
            PendingPlaylistSaveStatus.Pending);
        if (draft.Name != command.Name && !repositories.Drafts.TryReplace(
            draft with { Name = command.Name, Revision = checked(draft.Revision + 1) }, draft.Revision))
            return new Outcome<PendingPlaylistSave>.Conflict("stale-playlist-draft", draft.Revision);
        repositories.Operations.Add(pending);
        return new Outcome<PendingPlaylistSave>.Success(pending);
    }

    private static Outcome<PlaylistSavedReceipt> Complete(IPlaylistDraftWriteRepositories repositories, PendingPlaylistSave pending,
        PlaylistSavedReceipt receipt)
    {
        if (repositories.Operations.Find(pending.Key)?.Receipt is { } recorded)
            return new Outcome<PlaylistSavedReceipt>.Success(recorded);

        if (repositories.Drafts.Find(pending.DraftId) is { } draft && draft.BasePlaylist == pending.BasePlaylist)
        {
            var next = draft with { Revision = checked(draft.Revision + 1), BasePlaylist = new(receipt.PlaylistId, receipt.Revision),
                Name = draft.Revision == pending.DraftRevision ? pending.Name : draft.Name };
            if (!repositories.Drafts.TryReplace(next, draft.Revision))
                return new Outcome<PlaylistSavedReceipt>.Conflict("stale-playlist-draft", draft.Revision);
            receipt = receipt with { DraftRevision = next.Revision };
        }

        return repositories.Operations.TryReplace(pending with { Status = PendingPlaylistSaveStatus.Accepted, Receipt = receipt }, PendingPlaylistSaveStatus.Pending)
            ? new Outcome<PlaylistSavedReceipt>.Success(receipt)
            : new Outcome<PlaylistSavedReceipt>.Conflict("playlist-operation-state-changed", null);
    }

    private static ImmutableArray<PlaylistEntry> ApplyEdit(ImmutableArray<PlaylistEntry> current, PlaylistDraftEdit edit)
    {
        if (edit is PlaylistDraftEdit.Append append && !append.OrderedTrackIds.IsDefaultOrEmpty && append.OrderedTrackIds.All(id => id != Guid.Empty))
            return current.AddRange(append.OrderedTrackIds.Select(id => new PlaylistEntry(Guid.NewGuid(), id)));
        if (edit is PlaylistDraftEdit.Reorder reorder && !reorder.OrderedEntryIds.IsDefault && reorder.OrderedEntryIds.Length == current.Length)
        {
            var byId = current.ToDictionary(item => item.EntryId);
            if (reorder.OrderedEntryIds.Distinct().Count() == current.Length && reorder.OrderedEntryIds.All(byId.ContainsKey))
                return [.. reorder.OrderedEntryIds.Select(id => byId[id])];
        }
        if (edit is PlaylistDraftEdit.Remove remove && !remove.EntryIds.IsDefaultOrEmpty)
        {
            var removed = remove.EntryIds.ToHashSet();
            if (removed.Count == remove.EntryIds.Length && removed.IsSubsetOf(current.Select(item => item.EntryId)))
                return [.. current.Where(item => !removed.Contains(item.EntryId))];
        }
        return default;
    }

    private static Outcome<T> Invalid<T>(string code, string? field = null) => new Outcome<T>.Invalid([new(code, field)]);

    private static Outcome<T> CopyFailure<TSource, T>(Outcome<TSource> outcome) => outcome switch
    {
        Outcome<TSource>.Invalid invalid => new Outcome<T>.Invalid(invalid.Issues),
        Outcome<TSource>.Conflict conflict => new Outcome<T>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<TSource>.Cancelled => new Outcome<T>.Cancelled(),
        Outcome<TSource>.DependencyFailure dependency => new Outcome<T>.DependencyFailure(dependency.Code, dependency.Retryable),
        _ => throw new InvalidOperationException("Expected an unsuccessful playlist outcome.")
    };
}
