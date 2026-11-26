using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Claims private import bytes before copying and recovers interrupted cleanup from persistent ownership.</summary>
/// <remarks>2026-11-24: Prevent orphaned import copies and early upload for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed partial class LibraryBatchHandlers
{
    /// <summary>Invoked inside the mutation route after it selects local staging.</summary>
    internal async Task<Outcome<LibraryBatchChange.Import>> PrepareImportCopiesAsync(Guid operationId, Guid selectionToken,
        ImmutableArray<string> paths, CancellationToken token)
    {
        if (importFiles is null)
            return new Outcome<LibraryBatchChange.Import>.DependencyFailure("private-import-stage-unavailable", false);
        var claimed = await store.ExecuteAsync(repository =>
        {
            repository.ClaimImportCopies(scope, operationId);
            return Success(true);
        }, token).ConfigureAwait(false);
        if (claimed is not Outcome<bool>.Success)
            return CopyFailure<bool, LibraryBatchChange.Import>(claimed);
        var copied = await importFiles.CopyAsync(scope, operationId, paths, token).ConfigureAwait(false);
        return copied is Outcome<ImmutableArray<LibraryBatchImportFile>>.Success { Value: var files }
            ? Success(new LibraryBatchChange.Import(files, selectionToken)) : CopyFailure<ImmutableArray<LibraryBatchImportFile>, LibraryBatchChange.Import>(copied);
    }

    /// <summary>Reclaims copies after failed staging, revert, completed commit, or interrupted shutdown.</summary>
    public Task<Outcome<bool>> RecoverPrivateImportCopiesAsync(CancellationToken token) => importFiles is null
        ? Task.FromResult(Success(false)) : OnMutationLaneAsync(() => ReleasePrivateImportCopiesAsync(token), token);

    /// <summary>Confirms any sealed approval is withdrawn before clearing local entries or their private copies.</summary>
    /// <remarks>2026-11-25: Preserve the local review while a withdrawal is uncertain for FR-LIBRARY-002#ac-batch-revert.</remarks>
    private async Task<Outcome<LibraryBatchMode>> RevertAndReleaseCopiesAsync(RevertLibraryBatch command, CancellationToken token)
    {
        await publicationLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await store.ExecuteAsync(repository =>
            {
                var current = repository.Read(scope);
                return current.BatchId == command.Version.BatchId && current.Revision == command.Version.Revision
                    ? Success(current.Seal) : Conflict<LibraryBatchSeal?>("stale-library-batch-review", current.Revision);
            }, token).ConfigureAwait(false);
            if (pending is not Outcome<LibraryBatchSeal?>.Success { Value: var seal })
                return CopyFailure<LibraryBatchSeal?, LibraryBatchMode>(pending);
            if (seal is not null)
            {
                if (publisher is not IApprovedLibraryBatchWithdrawal withdrawal)
                    return Conflict<LibraryBatchMode>("library-batch-commit-in-progress", command.Version.Revision);
                var approval = seal.Command.Approval;
                var withdrawn = await withdrawal.WithdrawAsync(scope, approval, seal.OperationId, token).ConfigureAwait(false);
                if (withdrawn is not Outcome<LibraryBatchWithdrawalReceipt>.Success { Value: var receipt })
                    return CopyFailure<LibraryBatchWithdrawalReceipt, LibraryBatchMode>(withdrawn);
                if (receipt.Scope != scope || receipt.Approval != approval || receipt.OperationId != seal.OperationId)
                    return new Outcome<LibraryBatchMode>.DependencyFailure("library-batch-withdrawal-mismatch", true);
                token = CancellationToken.None;
            }
            return await OnMutationLaneAsync(async () =>
            {
                var reverted = await store.ExecuteAsync(repository => Revert(repository, command, seal), token).ConfigureAwait(false);
                if (reverted is not Outcome<LibraryBatchMode>.Success { Value: var mode } || importFiles is null)
                    return reverted;
                var cleanup = await ReleasePrivateImportCopiesAsync(CancellationToken.None).ConfigureAwait(false);
                return Success(mode with { ImportCleanupPending = cleanup is not Outcome<bool>.Success { Value: true } });
            }, token).ConfigureAwait(false);
        }
        finally { publicationLane.Release(); }
    }

    private async Task<Outcome<bool>> ReleasePrivateImportCopiesAsync(CancellationToken token)
    {
        var needed = await store.ExecuteAsync(repository => Success(ImportCopiesToRelease(repository, repository.Read(scope)).ToImmutableArray()), token)
            .ConfigureAwait(false);
        if (needed is not Outcome<ImmutableArray<Guid>>.Success { Value: var pending })
            return CopyFailure<ImmutableArray<Guid>, bool>(needed);

        var cleaned = true;
        foreach (var operationId in pending)
        {
            var discarded = await importFiles!.DiscardAsync(scope, operationId, token).ConfigureAwait(false);
            if (discarded is Outcome<bool>.Cancelled)
                return discarded;
            if (discarded is not Outcome<bool>.Success { Value: true })
            {
                cleaned = false;
                continue;
            }
            var saved = await store.ExecuteAsync(repository =>
            {
                repository.ReleaseImportCopies(scope, operationId);
                return Success(true);
            }, token).ConfigureAwait(false);
            if (saved is not Outcome<bool>.Success)
                return saved;
        }
        return Success(cleaned);
    }

    private IEnumerable<Guid> ImportCopiesToRelease(ILibraryBatchRepository repository, LibraryBatchRecord mode) =>
        repository.ImportCopyClaims(scope).Where(id => repository.FindStage(scope, id) is not { } stage || stage.Receipt.Version.BatchId != mode.BatchId);
}
