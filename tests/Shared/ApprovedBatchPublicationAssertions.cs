using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines the observed approval, recovery, and publication effects independently of the host and desktop stores.</summary>
/// <remarks>2026-11-24: Bind TEST-LIBRARY-003/004 for FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-commit.</remarks>
internal static class ApprovedBatchPublicationAssertions
{
    internal sealed record Evidence(bool PrivateBeforeApproval, bool UncertainCommitKeptSeal, bool RetriedOriginalOperation, long PublishedRevision,
        string[] PeerPlaylistNames, string[] PlaylistOrder, long FinalPlaylistVersion, bool PublishedOnce, bool RejectedStaleBatchAtomically,
        string[] MixedJournalOrder, bool MixedHasNoPublishedEffects, bool MixedSealRetained, bool ChangedReplayDenied, bool ForeignReadDenied,
        bool ChildReplayDenied, bool MaintenanceReserved, bool RevocationDenied);

    /// <summary>Checks that a full approval survives restart and that an unexecuted mixed batch stays unpublished.</summary>
    internal static void Verify(Evidence result)
    {
        Assert.True(result.PrivateBeforeApproval);
        Assert.True(result.UncertainCommitKeptSeal);
        Assert.True(result.RetriedOriginalOperation);
        Assert.Equal(21, result.PublishedRevision);
        Assert.Equal(["Final ordering"], result.PeerPlaylistNames);
        Assert.Equal(["Second", "First", "Second"], result.PlaylistOrder);
        Assert.Equal(2, result.FinalPlaylistVersion);
        Assert.True(result.PublishedOnce);
        Assert.True(result.RejectedStaleBatchAtomically);
        Assert.Equal(["TrackTags", "Import", "Maintenance", "Playlist"], result.MixedJournalOrder);
        Assert.True(result.MixedHasNoPublishedEffects);
        Assert.True(result.MixedSealRetained);
        Assert.True(result.ChangedReplayDenied);
        Assert.True(result.ForeignReadDenied);
        Assert.True(result.ChildReplayDenied);
        Assert.True(result.MaintenanceReserved);
        Assert.True(result.RevocationDenied);
    }
}
