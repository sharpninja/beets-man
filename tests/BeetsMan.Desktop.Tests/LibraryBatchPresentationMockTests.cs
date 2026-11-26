using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the independent batch command expectations detect unsafe or stale state.</summary>
/// <remarks>2026-11-25: Retain the mock gate for TEST-LIBRARY-003 and TEST-DISPATCH-001.</remarks>
/// <remarks>2026-11-26: Reject stale review and uncertain approval outcomes for FR-LIBRARY-002#ac-batch-review and FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed class LibraryBatchPresentationMockTests
{
    [Fact]
    public void RejectsUnsafeBatchPresentationOutcomes()
    {
        var expected = new LibraryBatchPresentationAssertions.Evidence(true, "Tags,Import", true, true, true, true, "Skip", 2, 1, true, true, true, true, true);
        LibraryBatchPresentationAssertions.Verify(expected);
        LibraryBatchPresentationAssertions.Evidence[] wrong =
        [
            expected with { CommitInitiallyDisabled = false }, expected with { ReviewOrder = "Import,Tags" },
            expected with { BlocksDuringCommit = false }, expected with { ReleasesAfterCommit = false },
            expected with { PreservedPendingFailure = false }, expected with { ResumeEnabled = false }, expected with { Choice = "KeepTags" },
            expected with { ChoicePosition = 1 }, expected with { ChoiceRevision = 2 }, expected with { CannotRepeatChoice = false },
            expected with { CannotCommitDuringSeal = false }, expected with { RevertEnabledWhileSealed = false },
            expected with { ClearedAfterRevert = false }, expected with { StaleReviewRejected = false }
        ];
        foreach (var result in wrong)
            Assert.ThrowsAny<XunitException>(() => LibraryBatchPresentationAssertions.Verify(result));
    }
}
