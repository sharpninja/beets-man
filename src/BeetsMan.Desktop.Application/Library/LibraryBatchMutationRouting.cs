using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Serializes the mode transition with normal library mutations and keeps replay on its original route.</summary>
/// <remarks>2026-11-24: Enforce the desktop boundary for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed partial class LibraryBatchHandlers
{
    private readonly SemaphoreSlim mutationLane = new(1, 1);

    /// <summary>Chooses direct or local mutation under the same mode lane. Restores a previously accepted stage without re-reading input.</summary>
    internal Task<Outcome<TResult>> RouteMutationAsync<TChange, TResult>(CommandContext context, Func<CancellationToken, Task<Outcome<TResult>>> direct,
        Func<CancellationToken, Task<Outcome<TChange>>> prepare, Predicate<TChange> matchesReplay,
        Func<TChange, LibraryBatchStageReceipt, TResult> project, CancellationToken token, BatchStageTransaction? transaction = null) where TChange : LibraryBatchChange
    {
        if (!Authorized(context.Request))
            return Denied<TResult>();
        if (context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<TResult>("invalid-operation-id"));
        return OnMutationLaneAsync(async () =>
        {
            var checkedRoute = await store.ExecuteAsync(repository => ReadMutationRoute(repository, context.OperationId), token).ConfigureAwait(false);
            if (checkedRoute is not Outcome<MutationRoute>.Success { Value: var route })
                return CopyFailure<MutationRoute, TResult>(checkedRoute);
            if (!route.IsLocal)
                return await direct(token).ConfigureAwait(false);
            if (route.Previous is { } previous)
                return previous.Change is TChange stored && matchesReplay(stored) ? Success(project(stored, previous.Receipt)) :
                    Conflict<TResult>("operation-intent-mismatch", previous.Receipt.Version.Revision);

            var prepared = await prepare(token).ConfigureAwait(false);
            if (prepared is not Outcome<TChange>.Success { Value: var change })
                return CopyFailure<TChange, TResult>(prepared);
            var staged = await StoreStageAsync(change, context.OperationId, token, transaction).ConfigureAwait(false);
            return staged is Outcome<LibraryBatchStageReceipt>.Success accepted ? Success(project(change, accepted.Value)) :
                CopyFailure<LibraryBatchStageReceipt, TResult>(staged);
        }, token);
    }

    /// <summary>Runs background publication only while the normal outbound route is active.</summary>
    internal Task<Outcome<bool>> ReplayDirectAsync(RequestContext context, Func<CancellationToken, Task<Outcome<bool>>> replay, CancellationToken token) =>
        RunWhenBatchDisabledAsync(context, replay, _ => Success(false), token);

    /// <summary>Defers an action on an already-hosted job until the desktop's batch has been resolved.</summary>
    internal Task<Outcome<TResult>> RunHostedJobAsync<TResult>(RequestContext context, Func<CancellationToken, Task<Outcome<TResult>>> send,
        CancellationToken token) => RunWhenBatchDisabledAsync(context, send,
            revision => Conflict<TResult>("library-batch-hosted-job-deferred", revision), token);

    private Task<Outcome<TResult>> RunWhenBatchDisabledAsync<TResult>(RequestContext context, Func<CancellationToken, Task<Outcome<TResult>>> action,
        Func<long, Outcome<TResult>> deferred, CancellationToken token) => Authorized(context) ? OnMutationLaneAsync(async () =>
        {
            var result = await store.ExecuteAsync(repository => Success(repository.Read(scope)), token).ConfigureAwait(false);
            if (result is not Outcome<LibraryBatchRecord>.Success { Value: var mode })
                return CopyFailure<LibraryBatchRecord, TResult>(result);
            return mode.BatchId.HasValue ? deferred(mode.Revision) : await action(token).ConfigureAwait(false);
        }, token) : Denied<TResult>();

    /// <summary>Resolves a prior stage without starting a new mutation or reserving a direct route.</summary>
    internal Task<Outcome<StagedMutation?>> ReadKnownStageAsync(CommandContext context, CancellationToken token) =>
        Authorized(context.Request) ? store.ExecuteAsync(repository => ReadKnownStage(repository, repository.Read(scope), context.OperationId), token) :
        Denied<StagedMutation?>();

    /// <summary>Reads the settled queue operation without replaying it or applying a reverted draft.</summary>
    /// <remarks>2026-11-24: Recover queue auto-save after a batch commits for FR-PLAYER-001#ac-queue-autosave.</remarks>
    internal Task<Outcome<PublishedBatchQueueSave?>> ReadPublishedQueueSaveAsync(OperationKey key, CancellationToken token) =>
        key.OriginInstanceId == scope.OriginInstanceId
            ? store.ExecuteAsync(repository => Success(repository.FindStage(scope, key.OperationId)?.QueuePublication), token)
            : Denied<PublishedBatchQueueSave?>();

    /// <summary>Returns null when batching is disabled, or the pending changes for just the selected track.</summary>
    internal Task<Outcome<TrackBatchOverlay?>> ReadTrackChangesAsync(Guid trackId, RequestContext context, CancellationToken token) =>
        Authorized(context) ? store.ExecuteAsync<TrackBatchOverlay?>(repository =>
        {
            var state = repository.Read(scope);
            return Success<TrackBatchOverlay?>(state.BatchId is { } batchId
                ? new(batchId, [.. ProjectReview(repository, state, batchId).Entries.Select(entry => entry.Change).OfType<LibraryBatchChange.TrackTags>()
                    .Where(change => change.TrackId == trackId)]) : null);
        }, token) : Denied<TrackBatchOverlay?>();

    /// <summary>Identifies the active batch and its ordered changes for one track.</summary>
    internal sealed record TrackBatchOverlay(Guid BatchId, ImmutableArray<LibraryBatchChange.TrackTags> Changes);

    private async Task<Outcome<TResult>> OnMutationLaneAsync<TResult>(Func<Task<Outcome<TResult>>> run, CancellationToken token)
    {
        try { await mutationLane.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<TResult>.Cancelled(); }
        try { return await run().ConfigureAwait(false); }
        finally { mutationLane.Release(); }
    }

    private Outcome<MutationRoute> ReadMutationRoute(ILibraryBatchRepository repository, Guid operationId)
    {
        var current = repository.Read(scope);
        if (repository.FindCommit(scope, operationId) is not null || current.Seal?.OperationId == operationId)
            return Conflict<MutationRoute>("operation-intent-mismatch", current.Revision);
        var known = ReadKnownStage(repository, current, operationId);
        if (known is not Outcome<StagedMutation?>.Success { Value: var stage })
            return CopyFailure<StagedMutation?, MutationRoute>(known);
        if (stage is not null)
            return Success(new MutationRoute(true, stage));

        var sentDirectly = repository.IsDirect(scope, operationId);
        if (current.BatchId is null)
        {
            if (!sentDirectly)
                repository.RecordDirect(scope, operationId);
            return Success(new MutationRoute(false));
        }
        if (sentDirectly)
            return Conflict<MutationRoute>("library-batch-operation-routed-directly", current.Revision);
        return current.Seal is null ? Success(new MutationRoute(true)) :
            Conflict<MutationRoute>("library-batch-commit-in-progress", current.Revision);
    }

    private Outcome<StagedMutation?> ReadKnownStage(ILibraryBatchRepository repository, LibraryBatchRecord current, Guid operationId)
    {
        if (repository.FindStage(scope, operationId) is not { } stage)
            return Success<StagedMutation?>(null);
        if (stage.Receipt.Version.BatchId != current.BatchId)
            return Conflict<StagedMutation?>("library-batch-closed", current.Revision);
        var entry = repository.FindEntry(scope, stage.Receipt.Version.BatchId, stage.Receipt.Position);
        if (entry is null || entry.OperationId != operationId)
            return new Outcome<StagedMutation?>.DependencyFailure("library-batch-entry-unavailable", false);
        return Success<StagedMutation?>(new(stage.Receipt, entry.Change));
    }

    private Task<Outcome<LibraryBatchStageReceipt>> StoreStageAsync(LibraryBatchChange change, Guid operationId, CancellationToken token,
        BatchStageTransaction? transaction = null)
    {
        if (operationId == Guid.Empty || !ValidChange(change))
            return Task.FromResult(Invalid<LibraryBatchStageReceipt>("invalid-library-batch-change"));
        if (change is LibraryBatchChange.TrackTags tags)
            change = tags with { Before = tags.Before.WithComparers(StringComparer.Ordinal), Changes = tags.Changes.WithComparers(StringComparer.Ordinal) };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(change)));
        return transaction is null ? store.ExecuteAsync(AddStage, token) : transaction(AddStage, token);

        Outcome<LibraryBatchStageReceipt> AddStage(ILibraryBatchRepository repository) => Stage(repository, change, operationId, fingerprint);
    }

    /// <summary>Commits the shared stage callback alongside a producer's local checkpoint.</summary>
    internal delegate Task<Outcome<LibraryBatchStageReceipt>> BatchStageTransaction(
        Func<ILibraryBatchRepository, Outcome<LibraryBatchStageReceipt>> stage, CancellationToken token);

    /// <summary>Provides the exact originally staged content and receipt.</summary>
    internal sealed record StagedMutation(LibraryBatchStageReceipt Receipt, LibraryBatchChange Change);

    private sealed record MutationRoute(bool IsLocal, StagedMutation? Previous = null);
}
