using Xunit;

/// <summary>Checks the exact outcomes of a revert across host admission and uncertain replies.</summary>
/// <remarks>2026-11-25: Cover FR-LIBRARY-002#ac-batch-revert and TEST-LIBRARY-004.</remarks>
internal static class BatchWithdrawalAssertions
{
    internal sealed record Evidence(string AcceptedPhase, string UnseenPhase, string LateSubmitCode, string AlreadyStartedCode,
        long FeedRevisionAfterWithdrawal, int LocalEntriesAfterLostReply, int LocalEntriesAfterRecovery, bool SourceRetained,
        bool ForeignCallerDenied, bool ScopeMismatchDenied, string ReplayedPhase);

    internal static void Verify(Evidence value)
    {
        Assert.Equal("Withdrawn", value.AcceptedPhase);
        Assert.Equal("Withdrawn", value.UnseenPhase);
        Assert.Equal("library-batch-withdrawn", value.LateSubmitCode);
        Assert.Equal("library-batch-commit-in-progress", value.AlreadyStartedCode);
        Assert.Equal(0, value.FeedRevisionAfterWithdrawal);
        Assert.Equal(1, value.LocalEntriesAfterLostReply);
        Assert.Equal(0, value.LocalEntriesAfterRecovery);
        Assert.True(value.SourceRetained);
        Assert.True(value.ForeignCallerDenied);
        Assert.True(value.ScopeMismatchDenied);
        Assert.Equal(value.AcceptedPhase, value.ReplayedPhase);
    }
}
