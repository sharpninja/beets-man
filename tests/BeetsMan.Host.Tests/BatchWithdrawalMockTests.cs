using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies that the withdrawal assertions reject loss of a local edit or a late host write.</summary>
/// <remarks>2026-11-25: Establish the independent TEST-LIBRARY-004 withdrawal gate before product edits.</remarks>
/// <remarks>2026-11-26: Prove the withdrawal and restore assertions for FR-LIBRARY-002#ac-batch-revert.</remarks>
public sealed class BatchWithdrawalMockTests
{
    [Fact]
    public void RejectsPrematureDiscardAndApprovalReplay()
    {
        var expected = new BatchWithdrawalAssertions.Evidence("Withdrawn", "Withdrawn", "library-batch-withdrawn", "library-batch-commit-in-progress",
            0, 1, 0, true, true, true, "Withdrawn");
        BatchWithdrawalAssertions.Verify(expected);
        BatchWithdrawalAssertions.Evidence[] wrong =
        [
            expected with { AcceptedPhase = "Accepted" }, expected with { UnseenPhase = "Accepted" }, expected with { LateSubmitCode = "accepted" },
            expected with { AlreadyStartedCode = "withdrawn" }, expected with { FeedRevisionAfterWithdrawal = 1 },
            expected with { LocalEntriesAfterLostReply = 0 }, expected with { LocalEntriesAfterRecovery = 1 }, expected with { SourceRetained = false },
            expected with { ForeignCallerDenied = false }, expected with { ScopeMismatchDenied = false }, expected with { ReplayedPhase = "Accepted" }
        ];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchWithdrawalAssertions.Verify(actual));
    }
}
