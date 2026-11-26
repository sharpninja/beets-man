using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Defines the observable batch and queue contract through a composed desktop workspace.</summary>
/// <remarks>2026-11-25: Verify FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit and ac-batch-revert with FR-PLAYER-001#ac-queue-autosave.</remarks>
internal static class RegisteredDesktopBatchAssertions
{
    internal sealed record Observation(bool LoadedFromReplica, bool BothEditsStaged, bool ReviewMatchesBothEdits,
        int WritesBeforeApproval, bool RevertPreservedHostVersion, int WritesForApprovedBatch, bool BothPublished,
        bool QueueAdoptedVersion, bool PeerReceivedExactOrder);

    /// <summary>Checks automatic delivery and acknowledgement after an approved batch.</summary>
    /// <remarks>2026-11-25: Verify FR-RECORDS-001#ac-replicate and ac-notify without a manual resume command.</remarks>
    internal sealed record AutomaticDelivery(bool OriginLoadedOnStartup, bool OnlinePeerStayedAtOriginalRevision,
        bool AppliedApprovedRevision, bool RecoveredLostAcknowledgement, bool PeerDidNotReplayAppliedRevision,
        bool OfflineRecipientRetainedNotification, bool OfflineRecipientCaughtUp);

    internal static void VerifyAutomaticDelivery(AutomaticDelivery actual)
    {
        Assert.True(actual.OriginLoadedOnStartup);
        Assert.True(actual.OnlinePeerStayedAtOriginalRevision);
        Assert.True(actual.AppliedApprovedRevision);
        Assert.True(actual.RecoveredLostAcknowledgement);
        Assert.True(actual.PeerDidNotReplayAppliedRevision);
        Assert.True(actual.OfflineRecipientRetainedNotification);
        Assert.True(actual.OfflineRecipientCaughtUp);
    }

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.LoadedFromReplica);
        Assert.True(actual.BothEditsStaged);
        Assert.True(actual.ReviewMatchesBothEdits);
        Assert.Equal(0, actual.WritesBeforeApproval);
        Assert.True(actual.RevertPreservedHostVersion);
        Assert.Equal(1, actual.WritesForApprovedBatch);
        Assert.True(actual.BothPublished);
        Assert.True(actual.QueueAdoptedVersion);
        Assert.True(actual.PeerReceivedExactOrder);
    }
}
