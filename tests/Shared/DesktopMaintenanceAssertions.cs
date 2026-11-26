using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Defines the desktop maintenance acceptance observations independently of either storage implementation.</summary>
/// <remarks>2026-11-24: Cover TEST-BEETS-002 and TEST-LIBRARY-003/004 for FR-BEETS-001#ac-edit, ac-recover and FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-revert.</remarks>
internal static class DesktopMaintenanceAssertions
{
    internal const string Source = "Inbox/Waiting.flac";
    internal const string Destination = "Lena Vale/Northern Lines/01 Harbor Lights.flac";
    internal const string Audio = "Inbox/Other.flac";

    internal sealed record Evidence(Guid FirstConfirmation, Guid RetriedConfirmation, Guid AcceptedJob, bool RetainedPreviewAfterLostReply,
        int RemoteConfirmations, int HostJobs, bool WrongApprovalRejected, bool CanConfirmOffline, string[] ReviewedEffects,
        bool OrderedTrackSelectionRetained, bool LocalReceiptsOnly, bool ReplayedAfterRestart, string ChangedReplay,
        int RemainingChanges, string LateReplay, bool CatalogAndMediaUnchanged);

    /// <summary>Checks the held preview, host replay, exact batch contents, and absence of effects after revert.</summary>
    internal static void Verify(Evidence evidence)
    {
        Assert.NotEqual(Guid.Empty, evidence.FirstConfirmation);
        Assert.Equal(evidence.FirstConfirmation, evidence.RetriedConfirmation);
        Assert.NotEqual(Guid.Empty, evidence.AcceptedJob);
        Assert.True(evidence.RetainedPreviewAfterLostReply);
        Assert.Equal(2, evidence.RemoteConfirmations);
        Assert.Equal(1, evidence.HostJobs);
        Assert.True(evidence.WrongApprovalRejected);
        Assert.True(evidence.CanConfirmOffline);
        Assert.Equal([$"{Source} -> {Destination}", "CatalogOnly:", $"CatalogAndAudio:{Audio}"], evidence.ReviewedEffects);
        Assert.True(evidence.OrderedTrackSelectionRetained);
        Assert.True(evidence.LocalReceiptsOnly);
        Assert.True(evidence.ReplayedAfterRestart);
        Assert.Equal("operation-intent-mismatch", evidence.ChangedReplay);
        Assert.Equal(0, evidence.RemainingChanges);
        Assert.Equal("library-batch-closed", evidence.LateReplay);
        Assert.True(evidence.CatalogAndMediaUnchanged);
    }
}
