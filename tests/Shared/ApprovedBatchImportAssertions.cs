using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines the approved import transfer observations independently of the production stores.</summary>
/// <remarks>2026-11-24: Bind TEST-LIBRARY-003/004 for FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-commit.</remarks>
internal static class ApprovedBatchImportAssertions
{
    internal static readonly string[] ReviewedBytes = ["original first audio", "original second audio"];

    internal sealed record Evidence(bool NoCopiesBeforeApproval, bool FirstReplyLostAndSealKept, int[] AcknowledgedAfterLoss,
        int[] SentOrdinals, int[] VerifiedOrdinals, string[] PrivateContents, bool RefusedBadDigest, bool RefusedForeignAndWrongFile,
        bool DirectImportIsolated, bool PreservedOperation, bool LocalCopiesRetained, bool BatchUnpublishedAndPeerFeedUnchanged);

    /// <summary>Checks exact bytes, restart continuation, and approval isolation.</summary>
    internal static void Verify(Evidence value)
    {
        Assert.True(value.NoCopiesBeforeApproval);
        Assert.True(value.FirstReplyLostAndSealKept);
        Assert.Equal([0], value.AcknowledgedAfterLoss);
        Assert.Equal([0, 1], value.SentOrdinals);
        Assert.Equal([0, 1], value.VerifiedOrdinals);
        Assert.Equal(ReviewedBytes, value.PrivateContents);
        Assert.True(value.RefusedBadDigest);
        Assert.True(value.RefusedForeignAndWrongFile);
        Assert.True(value.DirectImportIsolated);
        Assert.True(value.PreservedOperation);
        Assert.True(value.LocalCopiesRetained);
        Assert.True(value.BatchUnpublishedAndPeerFeedUnchanged);
    }
}
