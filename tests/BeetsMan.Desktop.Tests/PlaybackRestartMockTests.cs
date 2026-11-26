using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the independent restart playback checks reject unapproved and inconsistent starts.</summary>
/// <remarks>2026-11-25: Retain the mock gate for TEST-PLAYER-001 and TEST-PLAYER-002.</remarks>
/// <remarks>2026-11-26: Prove the opt-in, exact occurrence and position checks for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public sealed class PlaybackRestartMockTests
{
    [Fact]
    public void RejectsImplicitStartsAndAnIncorrectSavedOccurrence()
    {
        var good = new PlaybackRestartAssertions.Evidence(0, 1, 1, true, TimeSpan.FromSeconds(37).Ticks, true, 0, 0, true, 0, true);
        PlaybackRestartAssertions.Verify(good);
        PlaybackRestartAssertions.Evidence[] wrong =
        [
            good with { StartsWithToggleOff = 1 }, good with { StartsWithToggleOn = 0 }, good with { StartsAfterRetry = 2 },
            good with { ExactOccurrence = false }, good with { BackendPositionTicks = 0 }, good with { SameHistoryOccurrence = false },
            good with { StartsForPausedPlaylist = 1 }, good with { StartsForUnbackedQueue = 1 }, good with { UnavailableIsRecoverable = false },
            good with { StartsFromQueries = 1 }, good with { ToggleStaysLocal = false }
        ];
        foreach (var value in wrong)
            Assert.ThrowsAny<XunitException>(() => PlaybackRestartAssertions.Verify(value));
    }
}
