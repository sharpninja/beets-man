using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Discriminates exact file organization from unsafe publication, replacement and replay.</summary>
/// <remarks>2026-11-24: Establish the mock gate for TEST-BEETS-002 and FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class OrganizationExecutionMockTests
{
    [Fact]
    public void RefusesStalePathsForeignBytesAndDuplicatePublication()
    {
        var expected = new OrganizationExecutionAssertions.Evidence(OrganizationExecutionAssertions.Source,
            OrganizationExecutionAssertions.Destination, true, "Running", OrganizationExecutionAssertions.Destination,
            true, true, true, "Published", 43, OrganizationExecutionAssertions.Destination, 1, 1, true, true, true, true);
        OrganizationExecutionAssertions.Verify(expected);
        OrganizationExecutionAssertions.Evidence[] wrong = [expected with { PreviewSource = "Unrelated.flac" },
            expected with { PreviewDestination = "Unapproved.flac" }, expected with { PreviewWasReadOnly = false },
            expected with { InterruptedPhase = "Published" }, expected with { NativePathAtInterruption = OrganizationExecutionAssertions.Source },
            expected with { OriginalsRetained = false }, expected with { DestinationVerified = false }, expected with { ConcurrentProjectionUnchanged = false },
            expected with { FinalPhase = "Running" }, expected with { CatalogRevision = 44 }, expected with { PeerPath = OrganizationExecutionAssertions.Source },
            expected with { NativePathWrites = 2 }, expected with { PublicationCount = 2 }, expected with { SameJobOnReplay = false },
            expected with { LateCollisionRejected = false }, expected with { ForeignBytesUnchanged = false }, expected with { AlternateDestinationAbsent = false }];
        foreach (var value in wrong)
            Assert.ThrowsAny<XunitException>(() => OrganizationExecutionAssertions.Verify(value));
    }
}
