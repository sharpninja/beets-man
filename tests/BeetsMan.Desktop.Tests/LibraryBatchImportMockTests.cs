using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks that the import batch assertions detect early uploads, changed bytes, and leaked copies.</summary>
/// <remarks>2026-11-24: Record the isolated test gate for TEST-LIBRARY-003 and FR-BEETS-001#ac-import.</remarks>
public sealed class LibraryBatchImportMockTests
{
    [Fact]
    public void RejectsAnEarlyUploadOrLeakedPrivateFiles()
    {
        var valid = new LibraryBatchImportObservation(["Harbor Lights.flac", "After Hours.flac"], 0, true, true, true, true, true,
            "library-batch-hosted-job-deferred", true, true, "library-batch-closed", 2, true);
        LibraryBatchImportAssertions.Verify(valid);
        Reject(valid with { ReviewedNames = ["After Hours.flac", "Harbor Lights.flac"] });
        Reject(valid with { HostTransfersInBatch = 1 });
        Reject(valid with { LocalReceipt = false });
        Reject(valid with { ReviewedHashesMatch = false });
        Reject(valid with { ReplayRecovered = false });
        Reject(valid with { CopyRetainedBytesAfterSourceChanged = false });
        Reject(valid with { FailedCopyLeftNoBatchEntry = false });
        Reject(valid with { HostedActionCode = "sent" });
        Reject(valid with { CopiesRemovedOnRevert = false });
        Reject(valid with { OriginalRetained = false });
        Reject(valid with { DelayedReplayCode = "accepted" });
        Reject(valid with { HostTransfersAfterDisable = 3 });
        Reject(valid with { OrdinaryImportAccepted = false });
    }

    private static void Reject(LibraryBatchImportObservation result) =>
        Assert.ThrowsAny<XunitException>(() => LibraryBatchImportAssertions.Verify(result));
}
