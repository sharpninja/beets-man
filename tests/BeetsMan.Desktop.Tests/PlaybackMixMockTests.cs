using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the mixer assertions reject audible leakage, wrong gain, and lost EQ bypass.</summary>
/// <remarks>2026-11-25: Verify independent audio mix controls before the implementation of FR-PLAYER-001#ac-mix.</remarks>
public sealed class PlaybackMixMockTests
{
    [Fact]
    public void AudioMixAssertionsRejectIndependentWrongMeasurements()
    {
        var fixture = new PlaybackMixAssertions.Observation(true, 0, 0, 0.5, 0.5, 0.4, 1, 0.5, 0.5, 1.995, 1, 1, true, true);
        PlaybackMixAssertions.Verify(fixture);
        PlaybackMixAssertions.Observation[] wrong =
        [
            fixture with { BothDecksPlayed = false },
            fixture with { LeftLeak = 0.3 },
            fixture with { RightLeak = 0.3 },
            fixture with { MidpointA = 0.707 },
            fixture with { MidpointB = 1 },
            fixture with { DeckGainRatio = 1 },
            fixture with { UnchangedDeckRatio = 0.4 },
            fixture with { MasterGainA = 1 },
            fixture with { MasterGainB = 1 },
            fixture with { EqBoost = 1 },
            fixture with { BypassRatio = 1.995 },
            fixture with { ResetRatio = 1.995 },
            fixture with { ManualBlendAbandonedFade = false },
            fixture with { InvalidControlsKeptMix = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => PlaybackMixAssertions.Verify(observation)));
    }
}
