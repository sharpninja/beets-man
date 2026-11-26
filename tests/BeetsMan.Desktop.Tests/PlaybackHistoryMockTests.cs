using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the playback history assertions reject duplicate and missing occurrences.</summary>
/// <remarks>2026-11-25: Establish the independent mock gate for TEST-RECORDS-001.</remarks>
/// <remarks>2026-11-26: Prove stable and distinct play identities in the acceptance fixture for FR-RECORDS-001#ac-offline.</remarks>
public sealed class PlaybackHistoryMockTests
{
    [Fact]
    public void RejectsPhantomPlaysAndRenumberedRetries()
    {
        var valid = new PlaybackHistoryAssertions.Evidence(1, 1, 2, 2, 3, 3, true, true, 4, 5, 5);
        PlaybackHistoryAssertions.Verify(valid);
        PlaybackHistoryAssertions.Evidence[] wrong =
        [
            valid with { FirstStart = 0 }, valid with { AfterPauseSeekAndResume = 2 }, valid with { AfterRepeatedQueueTrack = 1 },
            valid with { AfterCue = 3 }, valid with { AfterSecondDeckStart = 2 }, valid with { AfterBackendRejection = 4 },
            valid with { FailedCheckpointLeftNoNewPlay = false }, valid with { OriginalPlayRetried = false },
            valid with { AfterRestartResume = 5 }, valid with { AfterExplicitStopAndPlay = 4 }, valid with { DistinctPlayIds = 4 }
        ];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => PlaybackHistoryAssertions.Verify(actual));
    }
}
