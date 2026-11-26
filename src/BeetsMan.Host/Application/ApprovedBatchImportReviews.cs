using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies one privately staged album inside its exact approval and entry.</summary>
/// <remarks>2026-11-24: Scope private guided review to FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
internal sealed record ApprovedBatchImportReviewWork(Guid ApprovalOperationId, int Position, ImportUpload Upload);

/// <summary>Reads verified upload scope and stores immutable proposals and explicit decisions.</summary>
internal interface IApprovedBatchImportReviewStore
{
    Task<HostOutcome<ApprovedBatchImportReviewWork?>> ReadNextBatchImportReviewAsync(Guid library, CancellationToken token);
    Task<HostOutcome<bool>> OfferBatchImportReviewAsync(ApprovedBatchImportReviewWork work, ImmutableArray<BeetsImportCandidate> candidates, CancellationToken token);
    Task<HostOutcome<ApprovedBatchImportChoiceReceipt>> ChooseBatchImportAsync(Guid library, AuthenticatedHostInstance caller, Guid approval,
        int position, ChooseApprovedBatchImportRequest request, CancellationToken token);
}

/// <summary>Validates an explicit guided choice without applying native effects.</summary>
internal sealed class HostApprovedBatchImportReviews(IApprovedBatchImportReviewStore store)
{
    /// <summary>Records an action for the authenticated owner and exact approved album revision.</summary>
    public Task<HostOutcome<ApprovedBatchImportChoiceReceipt>> ChooseAsync(Guid library, AuthenticatedHostInstance caller, Guid approval, int position,
        ChooseApprovedBatchImportRequest request, CancellationToken token)
    {
        if (caller.InstanceId != request.OriginInstanceId)
            return Task.FromResult<HostOutcome<ApprovedBatchImportChoiceReceipt>>(new HostOutcome<ApprovedBatchImportChoiceReceipt>.Forbidden());
        if (approval == Guid.Empty || position < 1 || request.EntryOperationId == Guid.Empty || request.OperationId == Guid.Empty ||
            request.ReviewItemId == Guid.Empty || request.ExpectedRevision <= 0 || !HostImportReview.ValidChoice(request.Choice, request.MatchId))
            return Task.FromResult<HostOutcome<ApprovedBatchImportChoiceReceipt>>(new HostOutcome<ApprovedBatchImportChoiceReceipt>.Rejected("invalid-batch-import-choice"));
        return store.ChooseBatchImportAsync(library, caller, approval, position, request, token);
    }
}

/// <summary>Runs only guided discovery against complete private copies in the batch namespace.</summary>
internal sealed class ApprovedBatchImportReviewWorker(Guid library, IApprovedBatchImportReviewStore store, Func<Guid, IBeetsGuidedImport> native)
{
    /// <summary>Offers one review after all its copies are verified.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var next = await store.ReadNextBatchImportReviewAsync(library, token).ConfigureAwait(false);
        if (next is not HostOutcome<ApprovedBatchImportReviewWork?>.Success { Value: var work })
            return next.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);
        var discovered = await native(work.ApprovalOperationId).DiscoverAsync(work.Upload, token).ConfigureAwait(false);
        if (discovered is not HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success { Value: var candidates })
            return discovered.PropagateFailure<bool>();
        return HostImportReview.ValidProposals(candidates) && candidates.All(candidate =>
            candidate.Comparison is not { } comparison || comparison.Tracks.Length == work.Upload.Files.Length)
            ? await store.OfferBatchImportReviewAsync(work, candidates, token).ConfigureAwait(false)
            : new HostOutcome<bool>.Unavailable("invalid-batch-import-proposals");
    }
}
