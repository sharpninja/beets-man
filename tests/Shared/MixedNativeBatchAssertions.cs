using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines the observed effects of one approved native batch across an interrupted run.</summary>
/// <remarks>2026-11-24: Bind TEST-LIBRARY-003/004 for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal static class MixedNativeBatchAssertions
{
    internal sealed record Evidence(string InterruptedPhase, long InterruptedFeed, string InterruptedGenre, bool OriginalProjectionHeld,
        bool CompetingWriteBlocked, string FinalPhase, long FinalFeed, string FinalGenre, bool RemovedOnlyFromCatalog, int TagWrites,
        int RemoveWrites, int PublicationCount, string[] PublishedKinds, string[] PeerTracks, string[] PeerPlaylist, long PeerCursor,
        bool SameApproval, bool NoSecondPublication, bool StaleNativeRejectedBeforeWrite);

    /// <summary>Verifies private progress, durable recovery, and the final peer-visible revision.</summary>
    internal static void Verify(Evidence value)
    {
        Assert.Equal("Running", value.InterruptedPhase);
        Assert.Equal(42, value.InterruptedFeed);
        Assert.Equal("Downtempo", value.InterruptedGenre);
        Assert.True(value.OriginalProjectionHeld);
        Assert.True(value.CompetingWriteBlocked);
        Assert.Equal("Published", value.FinalPhase);
        Assert.Equal(43, value.FinalFeed);
        Assert.Equal("Downtempo", value.FinalGenre);
        Assert.True(value.RemovedOnlyFromCatalog);
        Assert.Equal(1, value.TagWrites);
        Assert.Equal(1, value.RemoveWrites);
        Assert.Equal(1, value.PublicationCount);
        Assert.Equal(["Track:Upsert", "Track:Delete", "Playlist:Upsert"], value.PublishedKinds);
        Assert.Equal(["Harbor"], value.PeerTracks);
        Assert.Equal(["Harbor", "Harbor"], value.PeerPlaylist);
        Assert.Equal(43, value.PeerCursor);
        Assert.True(value.SameApproval);
        Assert.True(value.NoSecondPublication);
        Assert.True(value.StaleNativeRejectedBeforeWrite);
    }
}
