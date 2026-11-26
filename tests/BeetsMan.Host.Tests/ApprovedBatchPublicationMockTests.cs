using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Rejects known bad publication, admission, and replay observations before binding the real stores.</summary>
/// <remarks>2026-11-24: Establish the mock gate for FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-commit.</remarks>
public sealed class ApprovedBatchPublicationMockTests
{
    /// <summary>Proves the acceptance harness detects a partial, unapproved, or cross-origin publication.</summary>
    [Fact]
    public void RejectsEarlyPartialAndMismatchedPublication()
    {
        var expected = new ApprovedBatchPublicationAssertions.Evidence(true, true, true, 21, ["Final ordering"], ["Second", "First", "Second"],
            2, true, true, ["TrackTags", "Import", "Maintenance", "Playlist"], true, true, true, true, true, true, true);
        ApprovedBatchPublicationAssertions.Verify(expected);
        ApprovedBatchPublicationAssertions.Evidence[] rejected =
        [
            expected with { PrivateBeforeApproval = false }, expected with { UncertainCommitKeptSeal = false },
            expected with { RetriedOriginalOperation = false }, expected with { PublishedRevision = 22 },
            expected with { PeerPlaylistNames = ["First draft", "Final ordering"] }, expected with { PlaylistOrder = ["First", "Second"] },
            expected with { FinalPlaylistVersion = 1 }, expected with { PublishedOnce = false }, expected with { RejectedStaleBatchAtomically = false },
            expected with { MixedJournalOrder = ["Playlist", "Import", "Maintenance", "TrackTags"] },
            expected with { MixedHasNoPublishedEffects = false }, expected with { MixedSealRetained = false },
            expected with { ChangedReplayDenied = false }, expected with { ForeignReadDenied = false }, expected with { ChildReplayDenied = false },
            expected with { MaintenanceReserved = false }, expected with { RevocationDenied = false }
        ];
        Assert.All(rejected, item => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ApprovedBatchPublicationAssertions.Verify(item)));
    }
}
