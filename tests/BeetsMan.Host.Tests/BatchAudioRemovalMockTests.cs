using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the independent expectations for an explicitly approved mixed audio removal.</summary>
/// <remarks>2026-11-24: Establish the TEST-LIBRARY-004 and TEST-BEETS-002 mock gate before batch execution changes.</remarks>
/// <remarks>2026-11-26: Verify that the approved batch contains the reviewed audio-removal intent for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed class BatchAudioRemovalMockTests
{
    [Fact]
    public void RefusesUnreviewedPathsAndRestartsWithoutRepeatingEffects()
    {
        const string organized = "Lena Vale/Northern Lines/02 Night Crossing.flac";
        var expected = new BatchAudioRemovalAssertions.Evidence(true, ["Inbox/Other.flac", OrganizationExecutionAssertions.Destination],
            organized, ["Inbox/Other.flac"], true, true, true, 1, 43, true, true, true, 2);
        BatchAudioRemovalAssertions.Verify(expected);
        BatchAudioRemovalAssertions.Evidence[] wrong = [expected with { HeldProjectionDuringRestart = false }, expected with { TagPaths = [organized] },
            expected with { CatalogRemovalPath = "Inbox/Other.flac" }, expected with { DeletedPaths = [organized] },
            expected with { DeletedPaths = ["Inbox/Other.flac", organized] }, expected with { OrganizedCopyKeptItsApprovedTags = false },
            expected with { OtherOriginalRetained = false }, expected with { FinalPeerState = false }, expected with { Publications = 2 },
            expected with { Revision = 42 }, expected with { ReplacedAudioRejectedWithoutWrites = false },
            expected with { CollisionRejectedWithoutWrites = false }, expected with { StableReplay = false }, expected with { Copies = 3 }];
        foreach (var observation in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchAudioRemovalAssertions.Verify(observation));
    }
}
