using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Observes batched source copies, transport guards, and revert cleanup.</summary>
/// <remarks>2026-11-24: Cover FR-LIBRARY-002#ac-batch-local and ac-batch-revert and FR-BEETS-001#ac-import.</remarks>
internal sealed record LibraryBatchImportObservation(string[] ReviewedNames, int HostTransfersInBatch, bool LocalReceipt, bool ReviewedHashesMatch,
    bool ReplayRecovered, bool CopyRetainedBytesAfterSourceChanged, bool FailedCopyLeftNoBatchEntry, string HostedActionCode,
    bool CopiesRemovedOnRevert, bool OriginalRetained, string DelayedReplayCode, int HostTransfersAfterDisable, bool OrdinaryImportAccepted);

/// <summary>Checks the exact copy and replay boundaries across desktop restart.</summary>
internal static class LibraryBatchImportAssertions
{
    public static void Verify(LibraryBatchImportObservation result)
    {
        Assert.Equal(["Harbor Lights.flac", "After Hours.flac"], result.ReviewedNames);
        Assert.Equal(0, result.HostTransfersInBatch);
        Assert.True(result.LocalReceipt);
        Assert.True(result.ReviewedHashesMatch);
        Assert.True(result.ReplayRecovered);
        Assert.True(result.CopyRetainedBytesAfterSourceChanged);
        Assert.True(result.FailedCopyLeftNoBatchEntry);
        Assert.Equal("library-batch-hosted-job-deferred", result.HostedActionCode);
        Assert.True(result.CopiesRemovedOnRevert);
        Assert.True(result.OriginalRetained);
        Assert.Equal("library-batch-closed", result.DelayedReplayCode);
        Assert.Equal(2, result.HostTransfersAfterDisable);
        Assert.True(result.OrdinaryImportAccepted);
    }
}
