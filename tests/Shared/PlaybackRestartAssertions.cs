using Xunit;

/// <summary>Checks that startup audio depends on the persisted desktop preference and eligible checkpoint.</summary>
/// <remarks>2026-11-25: Cover FR-PLAYER-001#ac-resume-playback-after-restart in TEST-PLAYER-001 and TEST-PLAYER-002.</remarks>
internal static class PlaybackRestartAssertions
{
    internal sealed record Evidence(int StartsWithToggleOff, int StartsWithToggleOn, int StartsAfterRetry, bool ExactOccurrence,
        long BackendPositionTicks, bool SameHistoryOccurrence, int StartsForPausedPlaylist, int StartsForUnbackedQueue,
        bool UnavailableIsRecoverable, int StartsFromQueries, bool ToggleStaysLocal);

    internal static void Verify(Evidence value)
    {
        Assert.Equal(0, value.StartsWithToggleOff);
        Assert.Equal(1, value.StartsWithToggleOn);
        Assert.Equal(1, value.StartsAfterRetry);
        Assert.True(value.ExactOccurrence);
        Assert.Equal(TimeSpan.FromSeconds(37).Ticks, value.BackendPositionTicks);
        Assert.True(value.SameHistoryOccurrence);
        Assert.Equal(0, value.StartsForPausedPlaylist);
        Assert.Equal(0, value.StartsForUnbackedQueue);
        Assert.True(value.UnavailableIsRecoverable);
        Assert.Equal(0, value.StartsFromQueries);
        Assert.True(value.ToggleStaysLocal);
    }
}
