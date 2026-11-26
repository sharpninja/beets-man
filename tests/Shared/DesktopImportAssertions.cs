using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Captures the existing desktop commands across selection, streamed upload and job recovery.</summary>
/// <remarks>2026-11-24: Prove desktop import behavior for FR-BEETS-001#ac-import, ac-recover, and FR-DISPATCH-001#ac-responsive.</remarks>
internal sealed record DesktopImportObservation(string[] SelectedNames, string[] Calls, bool CancelledSelection,
    bool RejectedForeignSelection, bool RetainedSelectionOnFailure, bool ReleasedSelectionAfterAcceptance,
    bool UsedSameUploadAndOperation, bool ChangedSourceRejected, bool SourceBytesIntact, Guid JobAfterRetry, Guid InspectedJob,
    string InspectedPhase, bool StayedInteractive);

/// <summary>Applies the same upload and retry assertions to scripted and production paths.</summary>
internal static class DesktopImportAssertions
{
    /// <summary>Requires an ordered upload and one recoverable job under the original request scope.</summary>
    public static void Verify(DesktopImportObservation actual)
    {
        Assert.Equal(["Selected A.flac", "Selected B.flac"], actual.SelectedNames);
        Assert.Equal(["manifest", "part:0", "part:1", "start", "start", "job"], actual.Calls);
        Assert.True(actual.CancelledSelection);
        Assert.True(actual.RejectedForeignSelection);
        Assert.True(actual.RetainedSelectionOnFailure);
        Assert.True(actual.ReleasedSelectionAfterAcceptance);
        Assert.True(actual.UsedSameUploadAndOperation);
        Assert.True(actual.ChangedSourceRejected);
        Assert.True(actual.SourceBytesIntact);
        Assert.NotEqual(Guid.Empty, actual.JobAfterRetry);
        Assert.Equal(actual.JobAfterRetry, actual.InspectedJob);
        Assert.Equal("Accepted", actual.InspectedPhase);
        Assert.True(actual.StayedInteractive);
    }
}
