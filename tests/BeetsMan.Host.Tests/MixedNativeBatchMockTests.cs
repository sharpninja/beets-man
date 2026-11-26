using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Shows that the mixed-batch evidence detects a lost, repeated, or prematurely published native effect.</summary>
/// <remarks>2026-11-24: Establish mock evidence for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
public sealed class MixedNativeBatchMockTests
{
    /// <summary>Rejects independently scripted failures at each part of the batch lifecycle.</summary>
    [Fact]
    public void RejectsPartialAndRepeatedNativeBatchEffects()
    {
        var expected = new MixedNativeBatchAssertions.Evidence("Running", 42, "Downtempo", true, true, "Published", 43, "Downtempo", true,
            1, 1, 1, ["Track:Upsert", "Track:Delete", "Playlist:Upsert"], ["Harbor"], ["Harbor", "Harbor"], 43, true, true, true);
        MixedNativeBatchAssertions.Verify(expected);
        MixedNativeBatchAssertions.Evidence[] rejected =
        [
            expected with { InterruptedPhase = "Published" }, expected with { InterruptedFeed = 43 }, expected with { InterruptedGenre = "Ambient" },
            expected with { OriginalProjectionHeld = false }, expected with { CompetingWriteBlocked = false }, expected with { FinalPhase = "Running" },
            expected with { FinalFeed = 44 }, expected with { FinalGenre = "Ambient" }, expected with { RemovedOnlyFromCatalog = false },
            expected with { TagWrites = 2 }, expected with { RemoveWrites = 2 }, expected with { PublicationCount = 2 },
            expected with { PublishedKinds = ["Track:Upsert", "Playlist:Upsert"] }, expected with { PeerTracks = ["Harbor", "Removed"] },
            expected with { PeerPlaylist = ["Harbor"] }, expected with { PeerCursor = 42 }, expected with { SameApproval = false },
            expected with { NoSecondPublication = false }, expected with { StaleNativeRejectedBeforeWrite = false }
        ];
        Assert.All(rejected, item => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MixedNativeBatchAssertions.Verify(item)));
    }
}
