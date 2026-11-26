using Xunit;

/// <summary>Checks offline listening intents, replay and recoverable conflicts.</summary>
/// <remarks>2026-11-25: Cover FR-RECORDS-001#ac-offline and TEST-RECORDS-002.</remarks>
internal static class LibraryRecordOutboxAssertions
{
    internal sealed record Evidence(int PendingBeforeReconnect, bool LatestOfflineFavorite, bool ReplayedOriginalOperations,
        long SecondFavoriteBaseline, int DistinctPlayedIds, int PendingAfterReplay, bool RetriedLostReply,
        bool ConflictSurvivesSnapshot, bool ReappliedAtReviewedRevision, bool KeepHostClearsDraft);

    internal static void Verify(Evidence value)
    {
        Assert.Equal(3, value.PendingBeforeReconnect);
        Assert.False(value.LatestOfflineFavorite);
        Assert.True(value.ReplayedOriginalOperations);
        Assert.Equal(1, value.SecondFavoriteBaseline);
        Assert.Equal(1, value.DistinctPlayedIds);
        Assert.Equal(0, value.PendingAfterReplay);
        Assert.True(value.RetriedLostReply);
        Assert.True(value.ConflictSurvivesSnapshot);
        Assert.True(value.ReappliedAtReviewedRevision);
        Assert.True(value.KeepHostClearsDraft);
    }
}
