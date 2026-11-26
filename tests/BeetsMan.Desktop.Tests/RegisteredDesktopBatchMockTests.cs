using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks that the composed batch assertions detect early writes and incorrect results.</summary>
/// <remarks>2026-11-25: Prove FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit and ac-batch-revert with independent controls.</remarks>
public sealed class RegisteredDesktopBatchMockTests
{
    /// <summary>Proves that the automatic notification checks reject missed or prematurely applied updates.</summary>
    /// <remarks>2026-11-25: Exercise independent controls for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
    [Fact]
    public void AutomaticDeliveryAssertionsRejectIncorrectReplicaOutcomes()
    {
        var fixture = new RegisteredDesktopBatchAssertions.AutomaticDelivery(true, true, true, true, true, true, true);
        RegisteredDesktopBatchAssertions.VerifyAutomaticDelivery(fixture);
        RegisteredDesktopBatchAssertions.AutomaticDelivery[] wrong =
        [
            fixture with { OriginLoadedOnStartup = false }, fixture with { OnlinePeerStayedAtOriginalRevision = false },
            fixture with { AppliedApprovedRevision = false }, fixture with { RecoveredLostAcknowledgement = false },
            fixture with { PeerDidNotReplayAppliedRevision = false }, fixture with { OfflineRecipientRetainedNotification = false },
            fixture with { OfflineRecipientCaughtUp = false }
        ];
        Assert.All(wrong, actual => Assert.ThrowsAny<XunitException>(() => RegisteredDesktopBatchAssertions.VerifyAutomaticDelivery(actual)));
    }

    [Fact]
    public void AssertionsRejectEarlyWritesAndMissingResults()
    {
        var fixture = new RegisteredDesktopBatchAssertions.Observation(true, true, true, 0, true, 1, true, true, true);
        RegisteredDesktopBatchAssertions.Verify(fixture);
        RegisteredDesktopBatchAssertions.Observation[] wrong =
        [
            fixture with { LoadedFromReplica = false },
            fixture with { BothEditsStaged = false },
            fixture with { ReviewMatchesBothEdits = false },
            fixture with { WritesBeforeApproval = 1 },
            fixture with { RevertPreservedHostVersion = false },
            fixture with { WritesForApprovedBatch = 2 },
            fixture with { BothPublished = false },
            fixture with { QueueAdoptedVersion = false },
            fixture with { PeerReceivedExactOrder = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => RegisteredDesktopBatchAssertions.Verify(observation)));
    }
}
