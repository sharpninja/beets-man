using Xunit;

/// <summary>Distinguishes an actual play from a continued or unstarted deck occurrence.</summary>
/// <remarks>2026-11-25: Cover FR-RECORDS-001#ac-replicate and ac-offline with TEST-RECORDS-001 and TEST-RECORDS-002.</remarks>
internal static class PlaybackHistoryAssertions
{
    internal sealed record Evidence(int FirstStart, int AfterPauseSeekAndResume, int AfterRepeatedQueueTrack, int AfterCue,
        int AfterSecondDeckStart, int AfterBackendRejection, bool FailedCheckpointLeftNoNewPlay, bool OriginalPlayRetried,
        int AfterRestartResume, int AfterExplicitStopAndPlay, int DistinctPlayIds);

    internal static void Verify(Evidence value)
    {
        Assert.Equal(1, value.FirstStart);
        Assert.Equal(1, value.AfterPauseSeekAndResume);
        Assert.Equal(2, value.AfterRepeatedQueueTrack);
        Assert.Equal(2, value.AfterCue);
        Assert.Equal(3, value.AfterSecondDeckStart);
        Assert.Equal(3, value.AfterBackendRejection);
        Assert.True(value.FailedCheckpointLeftNoNewPlay);
        Assert.True(value.OriginalPlayRetried);
        Assert.Equal(4, value.AfterRestartResume);
        Assert.Equal(5, value.AfterExplicitStopAndPlay);
        Assert.Equal(5, value.DistinctPlayIds);
    }
}
