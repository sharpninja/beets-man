using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Observes real tag and playlist requests using the local batch boundary.</summary>
/// <remarks>2026-11-24: Exercise FR-LIBRARY-002#ac-batch-local and ac-batch-revert and TEST-LIBRARY-003/004.</remarks>
internal sealed record LibraryBatchMutationObservation(string CachedGenre, string StagedGenre, string RestoredGenre, long StagedTrackRevision,
    bool LocalTagReceipt, bool LocalPlaylistReceipt, string[] ReviewEntries, int HostTagReadsInBatch, int HostTagWritesInBatch,
    int HostPlaylistWritesInBatch, string ClosedTagReplay, string ClosedPlaylistReplay, bool OrdinaryDraftUnchangedByStaging,
    bool SameStagedReceipt, int HostTagWritesAfterDisable, int HostPlaylistWritesAfterDisable, bool PublishedOrdinaryPlaylist);

/// <summary>Detects bypasses and leaked or falsely published local edits.</summary>
internal static class LibraryBatchMutationAssertions
{
    public static void Verify(LibraryBatchMutationObservation result)
    {
        Assert.Equal("Ambient", result.CachedGenre);
        Assert.Equal("Downtempo", result.StagedGenre);
        Assert.Equal("Ambient", result.RestoredGenre);
        Assert.Equal(42, result.StagedTrackRevision);
        Assert.True(result.LocalTagReceipt);
        Assert.True(result.LocalPlaylistReceipt);
        Assert.Equal(["Ambient → Downtempo", "Night drive:2"], result.ReviewEntries);
        Assert.Equal(0, result.HostTagReadsInBatch);
        Assert.Equal(0, result.HostTagWritesInBatch);
        Assert.Equal(0, result.HostPlaylistWritesInBatch);
        Assert.Equal("library-batch-closed", result.ClosedTagReplay);
        Assert.Equal("library-batch-closed", result.ClosedPlaylistReplay);
        Assert.True(result.OrdinaryDraftUnchangedByStaging);
        Assert.True(result.SameStagedReceipt);
        Assert.Equal(1, result.HostTagWritesAfterDisable);
        Assert.Equal(1, result.HostPlaylistWritesAfterDisable);
        Assert.True(result.PublishedOrdinaryPlaylist);
    }
}
