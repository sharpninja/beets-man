using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the import upload assertions against independently scripted client and job outcomes.</summary>
/// <remarks>2026-11-24: Establish the mock gate for FR-BEETS-001#ac-import and ac-recover before writing the upload workflow.</remarks>
public sealed class ImportUploadMockTests
{
    /// <summary>Rejects corrupt copies, crossed scopes, premature jobs, replay duplication, and unreviewed catalog changes.</summary>
    [Fact]
    public void RejectsPrematureOrUnrecoverableImportAcceptance()
    {
        var provider = new ScriptedImportBoundary();
        var observation = provider.ObserveUpload();
        ImportUploadAssertions.Verify(observation);
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { UnauthenticatedStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { ForeignLibraryStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { OtherOwnerStatus = 200 }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { CorruptPartCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { IncompleteStartCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { ChangedManifestCode = "accepted" }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { PrivateNames = ["Side B.flac", "Side A.flac"] }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { OriginalContents = ["fixture audio A"] }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { StagedContents = ["fixture audio A", "partial"] }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { JobsBeforeComplete = 1 }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { JobsAfterRetry = 2 }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { RecoveredJob = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { RecoveredPhase = "Published" }));
        Assert.ThrowsAny<XunitException>(() => ImportUploadAssertions.Verify(observation with { FinalCatalogRevision = 18 }));
    }

    /// <summary>Supplies predetermined wire responses and copy readback for the documented upload sequence.</summary>
    private sealed class ScriptedImportBoundary
    {
        private readonly Guid job = new("0b53545f-a09d-45e7-b090-daa23a4626f4");
        private readonly Queue<int> deniedRequests = new([401, 403, 404]);
        private readonly Queue<string> commandFailures = new(["import-copy-mismatch", "import-upload-incomplete", "operation-intent-mismatch"]);
        private readonly Queue<int> acceptedJobs = new([0, 1]);

        public ImportUploadObservation ObserveUpload() => new(deniedRequests.Dequeue(), deniedRequests.Dequeue(), deniedRequests.Dequeue(),
            commandFailures.Dequeue(), commandFailures.Dequeue(), commandFailures.Dequeue(), ["Side A.flac", "Side B.flac"],
            ["fixture audio A", "fixture audio B"], ["fixture audio A", "fixture audio B"], acceptedJobs.Dequeue(), acceptedJobs.Dequeue(),
            job, job, "Accepted", 17, 17);
    }
}
