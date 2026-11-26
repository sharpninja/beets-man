using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the started library presentation from browse through a local playlist batch review.</summary>
/// <remarks>2026-11-25: Cover FR-LIBRARY-001#ac-browse, ac-offline, FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-revert and TEST-DISPATCH-001.</remarks>
internal static class LibraryWorkspacePresentationAssertions
{
    internal sealed record Evidence(bool OpenedOfflineCatalog, bool BoundSharedQueue, bool CopiedInDisplayedOrder, bool StartedWithLocalBatchMode,
        bool StageEnabledReview, bool ReviewListedExactDraft, bool StageInvalidatedEarlierApproval, bool RevertClearedStage, bool ClosedAllOwners);

    internal static void Verify(Evidence result)
    {
        Assert.True(result.OpenedOfflineCatalog);
        Assert.True(result.BoundSharedQueue);
        Assert.True(result.CopiedInDisplayedOrder);
        Assert.True(result.StartedWithLocalBatchMode);
        Assert.True(result.StageEnabledReview);
        Assert.True(result.ReviewListedExactDraft);
        Assert.True(result.StageInvalidatedEarlierApproval);
        Assert.True(result.RevertClearedStage);
        Assert.True(result.ClosedAllOwners);
    }
}
