using Xunit;

/// <summary>Checks the batch command sequence independently of the ViewModel.</summary>
/// <remarks>2026-11-25: Cover TEST-LIBRARY-003 and TEST-DISPATCH-001 for exact review, import decisions, and stale results.</remarks>
/// <remarks>2026-11-26: Share exact review and retry expectations for FR-LIBRARY-002#ac-batch-review and FR-LIBRARY-002#ac-batch-commit.</remarks>
internal static class LibraryBatchPresentationAssertions
{
    internal sealed record Evidence(bool CommitInitiallyDisabled, string ReviewOrder, bool BlocksDuringCommit, bool ReleasesAfterCommit,
        bool PreservedPendingFailure, bool ResumeEnabled, string Choice, int ChoicePosition, long ChoiceRevision, bool CannotRepeatChoice,
        bool CannotCommitDuringSeal, bool RevertEnabledWhileSealed, bool ClearedAfterRevert, bool StaleReviewRejected);

    internal static void Verify(Evidence value)
    {
        Assert.True(value.CommitInitiallyDisabled);
        Assert.Equal("Tags,Import", value.ReviewOrder);
        Assert.True(value.BlocksDuringCommit);
        Assert.True(value.ReleasesAfterCommit);
        Assert.True(value.PreservedPendingFailure);
        Assert.True(value.ResumeEnabled);
        Assert.Equal("Skip", value.Choice);
        Assert.Equal(2, value.ChoicePosition);
        Assert.Equal(1, value.ChoiceRevision);
        Assert.True(value.CannotRepeatChoice);
        Assert.True(value.CannotCommitDuringSeal);
        Assert.True(value.RevertEnabledWhileSealed);
        Assert.True(value.ClearedAfterRevert);
        Assert.True(value.StaleReviewRejected);
    }
}
