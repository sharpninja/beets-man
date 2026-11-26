using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Records the approval, staged proof and final catalog result for a native matched album.</summary>
/// <remarks>2026-11-24: Cover exact match application and recovery for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record ImportMatchApplyObservation(string DriftCode, bool DriftRetryable, bool NoLiveEffectsAfterDrift, bool FenceStayedClosedAfterDrift,
    Guid JobId, Guid PublishedJobId, long PublishedRevision, string[] AppliedTrackTitles, bool OriginalAndStagedBytesPreserved,
    bool TaggedCopiesChanged, bool PublishedMatchesApprovedRelease);

/// <summary>Requires a changed release to leave the library untouched and a valid repeat to publish only the approved match.</summary>
internal static class ImportMatchApplyAssertions
{
    public static void Verify(ImportMatchApplyObservation observed)
    {
        Assert.Equal("approved-import-match-changed", observed.DriftCode);
        Assert.True(observed.DriftRetryable);
        Assert.True(observed.NoLiveEffectsAfterDrift);
        Assert.True(observed.FenceStayedClosedAfterDrift);
        Assert.NotEqual(Guid.Empty, observed.JobId);
        Assert.Equal(observed.JobId, observed.PublishedJobId);
        Assert.Equal(42, observed.PublishedRevision);
        Assert.Equal(["North Revised", "South Revised"], observed.AppliedTrackTitles);
        Assert.True(observed.OriginalAndStagedBytesPreserved);
        Assert.True(observed.TaggedCopiesChanged);
        Assert.True(observed.PublishedMatchesApprovedRelease);
    }
}
