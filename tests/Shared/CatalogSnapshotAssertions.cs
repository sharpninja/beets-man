using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines the independent acceptance observations for an expired catalog replica.</summary>
/// <remarks>2026-11-24: Bind TEST-RECORDS-001/002 for FR-RECORDS-001#ac-replicate, ac-notify and ac-offline.</remarks>
internal static class CatalogSnapshotAssertions
{
    internal sealed record Evidence(bool ExpiryDetected, long PartialCursor, string[] PartialTracks, int EarlyAcknowledgements,
        long[] SnapshotUppers, long SnapshotCursor, long PlaylistRevisionAtSnapshot, string[] SnapshotTracks, long RecoveredCursor,
        long PlaylistRevisionAfterCatchUp, long[] Acknowledged, bool LocalDraftAndOperationRetained, bool OtherLibraryAndAudioRetained,
        bool ForeignSnapshotDenied);

    /// <summary>Verifies atomic replacement, catch-up, and preservation of private state.</summary>
    internal static void Verify(Evidence result)
    {
        Assert.True(result.ExpiryDetected);
        Assert.Equal(2, result.PartialCursor);
        Assert.Equal(["Old"], result.PartialTracks);
        Assert.Equal(0, result.EarlyAcknowledgements);
        Assert.Equal([7L, 7L, 7L, 7L, 7L], result.SnapshotUppers);
        Assert.Equal(7, result.SnapshotCursor);
        Assert.Equal(1, result.PlaylistRevisionAtSnapshot);
        Assert.Equal(["Survivor"], result.SnapshotTracks);
        Assert.Equal(8, result.RecoveredCursor);
        Assert.Equal(2, result.PlaylistRevisionAfterCatchUp);
        Assert.Equal([7L, 8L], result.Acknowledged);
        Assert.True(result.LocalDraftAndOperationRetained);
        Assert.True(result.OtherLibraryAndAudioRetained);
        Assert.True(result.ForeignSnapshotDenied);
    }
}
