using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the independent expectations for a mixed batch with verified organization.</summary>
/// <remarks>2026-11-24: Establish the mock gate for TEST-LIBRARY-004 and TEST-BEETS-002.</remarks>
/// <remarks>2026-11-26: Prove reviewed organization effects stay private until commit for FR-LIBRARY-002#ac-batch-local and FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed class BatchOrganizationMockTests
{
    [Fact]
    public void RejectsOldNativePathsEarlyPublicationAndUnsafeCopies()
    {
        var expected = new BatchOrganizationAssertions.Evidence(true, 2, OrganizationExecutionAssertions.Destination,
            "Lena Vale/Northern Lines/02 Night Crossing.flac", true, true, true, 1, 43,
            ["Track:Upsert", "Track:Delete", "Playlist:Upsert"], true, true, true, true);
        BatchOrganizationAssertions.Verify(expected);
        BatchOrganizationAssertions.Evidence[] wrong = [expected with { PrivateCheckpoint = false }, expected with { Copies = 3 },
            expected with { TagPath = OrganizationExecutionAssertions.Source }, expected with { RemovalPath = "Inbox/Night.flac" },
            expected with { RetainedOriginals = false }, expected with { EditedDestinationOnly = false }, expected with { RemovedFromCatalogOnly = false },
            expected with { Publications = 2 }, expected with { Revision = 44 }, expected with { Changes = ["Track:Upsert", "Track:Upsert", "Playlist:Upsert"] },
            expected with { CorrectPeerProjection = false }, expected with { ExactRetry = false }, expected with { RejectedCollision = false },
            expected with { CollisionBytesRetained = false }];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchOrganizationAssertions.Verify(actual));
    }
}
