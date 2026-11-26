using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the reconciliation and revision assertions independently of native beets and LiteDB.</summary>
/// <remarks>2026-11-24: Establish the mock gate for the publishing portion of FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
public sealed class ImportPublicationMockTests
{
    /// <summary>Detects partial publication, duplicate revisions, lost IDs, wrong recipients, or an uncorrelated job.</summary>
    [Fact]
    public void RejectsUnsafeOrUncorrelatedPublication()
    {
        var observed = ScriptedPublication.Observe();
        ImportPublicationAssertions.Verify(observed);
        Reject(observed with { RevisionAfterPartial = 42 });
        Reject(observed with { PublishedRevision = 41 });
        Reject(observed with { ReplayedRevision = 43 });
        Reject(observed with { PartialCode = "published" });
        Reject(observed with { WrongOperationCode = "published" });
        Reject(observed with { ChangedReplayCode = "published" });
        Reject(observed with { JobPhase = "Running" });
        Reject(observed with { JobRevision = 3 });
        Reject(observed with { PublishedExistingTrackId = Guid.NewGuid() });
        Reject(observed with { PublishedNativeTrackIds = [27L] });
        Reject(observed with { RelativePaths = ["../private/01 North.flac", observed.RelativePaths[1]] });
        Reject(observed with { ChangeKinds = ["Track", "Track"] });
        Reject(observed with { PublishedOperationId = Guid.NewGuid() });
        Reject(observed with { DeliveryRecipients = [Guid.NewGuid()] });
        Reject(observed with { ChangeSets = 2 });
    }

    private static void Reject(ImportPublicationObservation observed) =>
        Assert.ThrowsAny<XunitException>(() => ImportPublicationAssertions.Verify(observed));

    /// <summary>Supplies the complete and incomplete reconciliation outcomes independent of the store.</summary>
    private static class ScriptedPublication
    {
        public static ImportPublicationObservation Observe()
        {
            var operation = new Guid("7ad97847-a04f-49f4-ab70-a746efa88e36");
            var currentTrack = new Guid("b63a2d1d-30d2-4373-99f8-7a2eb75e616f");
            var recipient = new Guid("69a234db-12cc-4a62-a56e-679618a2c643");
            return new(41, 41, 42, 42, "import-reconciliation-incomplete", "import-publication-scope-mismatch",
                "import-publication-intent-mismatch", "Published", 4, currentTrack, currentTrack, [27, 28],
                ["Fixture Band/Fixture Album/01 North.flac", "Fixture Band/Fixture Album/02 South.flac"],
                ["Artist", "Album", "Track", "Track"], operation, operation, recipient, [recipient], 1);
        }
    }
}
