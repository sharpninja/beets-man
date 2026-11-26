using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves that track-tag execution assertions reject incorrect native or peer effects.</summary>
/// <remarks>2026-11-24: Establish mock evidence before implementation for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class TrackTagExecutionMockTests
{
    [Fact]
    public void RejectsIncorrectReadbackOrDuplicatePublication()
    {
        var job = Guid.Parse("86f517d0-4699-43e4-8740-bece5ee41091");
        var operation = Guid.Parse("0b717b64-2c50-4e1b-827a-632c31bd0c71");
        var recipient = Guid.Parse("ecc729d8-3ef4-4185-a9a1-31d749bb8a52");
        var valid = new TrackTagExecutionObservation(job, job, operation, operation, "Ambient", "Downtempo $title 100%", "Downtempo $title 100%",
            null, true, true, true, true, "Running", 42, "Published", 43, 1, 1, recipient, [recipient]);
        TrackTagExecutionAssertions.Verify(valid);

        Reject(valid with { RecoveredJobId = Guid.Empty });
        Reject(valid with { PublishedOperationId = Guid.Empty });
        Reject(valid with { OriginalGenre = "Rock" });
        Reject(valid with { RecoveredGenre = "Rock" });
        Reject(valid with { EmbeddedGenre = "Downtempo Harbor Lights 100%" });
        Reject(valid with { EmbeddedYear = "2023" });
        Reject(valid with { RecoveredAtOriginalPath = false });
        Reject(valid with { SourceBytesRetained = false });
        Reject(valid with { SiblingBytesRetained = false });
        Reject(valid with { EncodedAudioRetained = false });
        Reject(valid with { InterruptedPhase = "Published" });
        Reject(valid with { InterruptedFeedRevision = 43 });
        Reject(valid with { RecoveredPhase = "Running" });
        Reject(valid with { RecoveredFeedRevision = 44 });
        Reject(valid with { NativeInvocations = 2 });
        Reject(valid with { PublicationCount = 2 });
        Reject(valid with { NotifiedPeers = [recipient, Guid.NewGuid()] });
    }

    private static void Reject(TrackTagExecutionObservation invalid) =>
        Assert.ThrowsAny<XunitException>(() => TrackTagExecutionAssertions.Verify(invalid));
}
