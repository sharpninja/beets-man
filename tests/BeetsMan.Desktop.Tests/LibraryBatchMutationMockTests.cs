using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks every tag and playlist batching assertion against independent failure fixtures.</summary>
/// <remarks>2026-11-24: Establish the mock gate before implementation for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed class LibraryBatchMutationMockTests
{
    [Fact]
    public void RejectsEarlyPublicationsAndIncorrectLocalRestoration()
    {
        var good = new LibraryBatchMutationObservation("Ambient", "Downtempo", "Ambient", 42, true, true, ["Ambient → Downtempo", "Night drive:2"],
            0, 0, 0, "library-batch-closed", "library-batch-closed", true, true, 1, 1, true);
        LibraryBatchMutationAssertions.Verify(good);
        Reject(good with { CachedGenre = "Rock" });
        Reject(good with { StagedGenre = "Ambient" });
        Reject(good with { RestoredGenre = "Downtempo" });
        Reject(good with { StagedTrackRevision = 43 });
        Reject(good with { LocalTagReceipt = false });
        Reject(good with { LocalPlaylistReceipt = false });
        Reject(good with { ReviewEntries = ["Ambient → Downtempo"] });
        Reject(good with { HostTagReadsInBatch = 1 });
        Reject(good with { HostTagWritesInBatch = 1 });
        Reject(good with { HostPlaylistWritesInBatch = 1 });
        Reject(good with { ClosedTagReplay = "routed-host" });
        Reject(good with { ClosedPlaylistReplay = "routed-host" });
        Reject(good with { OrdinaryDraftUnchangedByStaging = false });
        Reject(good with { SameStagedReceipt = false });
        Reject(good with { HostTagWritesAfterDisable = 0 });
        Reject(good with { HostPlaylistWritesAfterDisable = 0 });
        Reject(good with { PublishedOrdinaryPlaylist = false });
    }

    private static void Reject(LibraryBatchMutationObservation bad) =>
        Assert.ThrowsAny<XunitException>(() => LibraryBatchMutationAssertions.Verify(bad));
}
