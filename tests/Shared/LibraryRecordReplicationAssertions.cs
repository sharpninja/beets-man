using Xunit;

/// <summary>Checks that listening and preference changes converge without duplicate publication.</summary>
/// <remarks>2026-11-25: Cover FR-RECORDS-001#ac-replicate and ac-offline and FR-SETTINGS-001#ac-availability.</remarks>
internal static class LibraryRecordReplicationAssertions
{
    internal sealed record Evidence(long FeedHead, int FavoriteDeletes, int DefaultDeletes, int DistinctPlays, bool AllReplaysStable,
        bool StaleFavoriteRejected, bool PlayIdentityConflictRejected, bool LocalOnlyDefaultRejected, bool ForeignOriginRejected,
        bool FeedAndSnapshotAgree, bool UnrelatedLocalStatePreserved);

    internal static void Verify(Evidence value)
    {
        Assert.Equal(6, value.FeedHead);
        Assert.Equal(1, value.FavoriteDeletes);
        Assert.Equal(1, value.DefaultDeletes);
        Assert.Equal(2, value.DistinctPlays);
        Assert.True(value.AllReplaysStable);
        Assert.True(value.StaleFavoriteRejected);
        Assert.True(value.PlayIdentityConflictRejected);
        Assert.True(value.LocalOnlyDefaultRejected);
        Assert.True(value.ForeignOriginRejected);
        Assert.True(value.FeedAndSnapshotAgree);
        Assert.True(value.UnrelatedLocalStatePreserved);
    }
}
