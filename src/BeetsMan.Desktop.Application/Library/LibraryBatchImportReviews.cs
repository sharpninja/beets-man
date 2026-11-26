using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Restricts guided review and choices to the current local seal.</summary>
/// <remarks>2026-11-24: Route private guided review for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
public sealed partial class LibraryBatchHandlers
{
    /// <inheritdoc />
    public async Task<Outcome<LibraryBatchImportReviews>> HandleAsync(GetLibraryBatchImportReviews query, RequestContext context, CancellationToken token)
    {
        var result = await ReadSealedImportReviewAsync(context, token).ConfigureAwait(false);
        if (result is not Outcome<SealedImportReview>.Success { Value: var approved })
            return CopyFailure<SealedImportReview, LibraryBatchImportReviews>(result);
        return publisher is IApprovedBatchImportReviewGateway gateway
            ? await gateway.ReadImportReviewsAsync(scope, approved.Review, approved.Seal.OperationId, token).ConfigureAwait(false)
            : new Outcome<LibraryBatchImportReviews>.DependencyFailure("batch-import-review-host-unavailable", true);
    }

    /// <inheritdoc />
    public async Task<Outcome<LibraryBatchImportChoiceReceipt>> HandleAsync(ChooseLibraryBatchImportAction command, CommandContext context,
        CancellationToken token)
    {
        if (command.Position < 1 || command.EntryOperationId == Guid.Empty || command.ReviewItemId == Guid.Empty || command.ExpectedRevision <= 0 ||
            command.ExpectedRevision == long.MaxValue || command.Action is null || context.OperationId == Guid.Empty)
            return Invalid<LibraryBatchImportChoiceReceipt>("invalid-batch-import-choice");
        var result = await ReadSealedImportReviewAsync(context.Request, token).ConfigureAwait(false);
        if (result is not Outcome<SealedImportReview>.Success { Value: var approved })
            return CopyFailure<SealedImportReview, LibraryBatchImportChoiceReceipt>(result);
        if (approved.Review.Version != command.Version || command.Position > approved.Review.Entries.Length ||
            approved.Review.Entries[command.Position - 1] is not { Change: LibraryBatchChange.Import, OperationId: var entry } || entry != command.EntryOperationId)
            return Conflict<LibraryBatchImportChoiceReceipt>("stale-library-batch-review", approved.Review.Version.Revision);
        return publisher is IApprovedBatchImportReviewGateway gateway
            ? await gateway.ChooseImportAsync(scope, approved.Seal.OperationId, command, context.OperationId, token).ConfigureAwait(false)
            : new Outcome<LibraryBatchImportChoiceReceipt>.DependencyFailure("batch-import-review-host-unavailable", true);
    }

    /// <summary>Loads the exact local review without sending or changing any of its entries.</summary>
    private Task<Outcome<SealedImportReview>> ReadSealedImportReviewAsync(RequestContext context, CancellationToken token) => Authorized(context)
        ? store.ExecuteAsync(repository =>
        {
            var current = repository.Read(scope);
            if (current.Seal is not { } seal || current.BatchId is not { } batchId)
                return Invalid<SealedImportReview>("library-batch-no-pending-commit");
            return Success(new SealedImportReview(seal, ProjectReview(repository, current, batchId)));
        }, token) : Denied<SealedImportReview>();

    private sealed record SealedImportReview(LibraryBatchSeal Seal, LibraryBatchReview Review);
}
