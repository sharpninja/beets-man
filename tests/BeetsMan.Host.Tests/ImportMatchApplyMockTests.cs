using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises approved-match publication assertions without using a native library.</summary>
/// <remarks>2026-11-24: Verify refusal of changed releases before wiring native apply for FR-BEETS-001#ac-import and ac-recover.</remarks>
public sealed class ImportMatchApplyMockTests
{
    [Fact]
    public void RequiresAnUnchangedReleaseAndPrivatePreparationBeforePublishing()
    {
        var job = new Guid("66dd4701-1bb0-4763-9cc4-dc39e80fd7f6");
        var observed = new ImportMatchApplyObservation("approved-import-match-changed", true, true, true, job, job, 42,
            ["North Revised", "South Revised"], true, true, true);
        ImportMatchApplyAssertions.Verify(observed);
        Reject(observed with { DriftCode = "Published" });
        Reject(observed with { DriftRetryable = false });
        Reject(observed with { NoLiveEffectsAfterDrift = false });
        Reject(observed with { FenceStayedClosedAfterDrift = false });
        Reject(observed with { PublishedJobId = Guid.NewGuid() });
        Reject(observed with { PublishedRevision = 43 });
        Reject(observed with { AppliedTrackTitles = ["South Revised", "North Revised"] });
        Reject(observed with { OriginalAndStagedBytesPreserved = false });
        Reject(observed with { TaggedCopiesChanged = false });
        Reject(observed with { PublishedMatchesApprovedRelease = false });
    }

    private static void Reject(ImportMatchApplyObservation observed) =>
        Assert.ThrowsAny<XunitException>(() => ImportMatchApplyAssertions.Verify(observed));
}
