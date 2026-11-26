using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the import copy assertions reject early, unverified, or unapproved transfers.</summary>
/// <remarks>2026-11-24: Establish independent mock evidence for FR-LIBRARY-002#ac-batch-local and ac-batch-commit.</remarks>
public sealed class ApprovedBatchImportMockTests
{
    /// <summary>Rejects a deliberately changed observation for each checked effect.</summary>
    [Fact]
    public void RejectsEarlyUploadReplayedPartAndUnverifiedSource()
    {
        var expected = new ApprovedBatchImportAssertions.Evidence(true, true, [0], [0, 1], [0, 1],
            ["original first audio", "original second audio"], true, true, true, true, true, true);
        ApprovedBatchImportAssertions.Verify(expected);
        ApprovedBatchImportAssertions.Evidence[] rejected =
        [
            expected with { NoCopiesBeforeApproval = false }, expected with { FirstReplyLostAndSealKept = false },
            expected with { AcknowledgedAfterLoss = [] }, expected with { SentOrdinals = [0, 0, 1] },
            expected with { VerifiedOrdinals = [0] }, expected with { PrivateContents = ["changed first audio", "original second audio"] },
            expected with { RefusedBadDigest = false }, expected with { RefusedForeignAndWrongFile = false }, expected with { DirectImportIsolated = false },
            expected with { PreservedOperation = false }, expected with { LocalCopiesRetained = false }, expected with { BatchUnpublishedAndPeerFeedUnchanged = false }
        ];
        Assert.All(rejected, item => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ApprovedBatchImportAssertions.Verify(item)));
    }
}
