using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines exact recipient, commit, and replay observations for the incremental catalog feed.</summary>
/// <remarks>2026-11-24: Verify TEST-RECORDS-001/002 for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
internal static class CatalogReplicationAssertions
{
    internal sealed record Evidence(long[] FixedUpperPages, long CursorAtFailedAck, long CursorAfterRetry, int[] AcknowledgedAttempts,
        string[] LocalTitles, string[] SavedPlaylistEntries, long SavedPlaylistRevision, bool DeletedTrackStaysDeleted,
        long[] OriginPending, long[] OnlinePeerPending, long[] OfflinePeerPending, string FailureCode, bool TamperedPageRolledBack,
        bool ScopeEnforced, bool LocalAudioRetained);

    /// <summary>Checks that a lost acknowledgement is recoverable without replaying committed pages or blocking another peer.</summary>
    internal static void Verify(Evidence value)
    {
        Assert.Equal([2L, 2L, 3L], value.FixedUpperPages);
        Assert.Equal(2, value.CursorAtFailedAck);
        Assert.Equal(3, value.CursorAfterRetry);
        Assert.Equal([2, 3], value.AcknowledgedAttempts);
        Assert.Equal(["Keep"], value.LocalTitles);
        Assert.Equal(["Keep", "Keep"], value.SavedPlaylistEntries);
        Assert.Equal(1, value.SavedPlaylistRevision);
        Assert.True(value.DeletedTrackStaysDeleted);
        Assert.Empty(value.OriginPending);
        Assert.Empty(value.OnlinePeerPending);
        Assert.Equal([1L, 2L, 3L], value.OfflinePeerPending);
        Assert.Equal("catalog-ack-host-unreachable", value.FailureCode);
        Assert.True(value.TamperedPageRolledBack);
        Assert.True(value.ScopeEnforced);
        Assert.True(value.LocalAudioRetained);
    }
}
