using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks measured native output against independent gain and equalizer expectations.</summary>
/// <remarks>2026-11-25: Verify per-deck transport and gain, manual blending and master EQ for FR-PLAYER-001#ac-mix.</remarks>
internal static class PlaybackMixAssertions
{
    internal sealed record Observation(bool BothDecksPlayed, double LeftLeak, double RightLeak, double MidpointA, double MidpointB,
        double DeckGainRatio, double UnchangedDeckRatio, double MasterGainA, double MasterGainB, double EqBoost, double BypassRatio,
        double ResetRatio, bool ManualBlendAbandonedFade, bool InvalidControlsKeptMix);

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.BothDecksPlayed);
        Assert.InRange(actual.LeftLeak, 0, 0.01);
        Assert.InRange(actual.RightLeak, 0, 0.01);
        Assert.InRange(actual.MidpointA, 0.48, 0.52);
        Assert.InRange(actual.MidpointB, 0.48, 0.52);
        Assert.InRange(actual.DeckGainRatio, 0.38, 0.42);
        Assert.InRange(actual.UnchangedDeckRatio, 0.98, 1.02);
        Assert.InRange(actual.MasterGainA, 0.48, 0.52);
        Assert.InRange(actual.MasterGainB, 0.48, 0.52);
        Assert.InRange(actual.EqBoost, 1.85, 2.15);
        Assert.InRange(actual.BypassRatio, 0.98, 1.02);
        Assert.InRange(actual.ResetRatio, 0.98, 1.02);
        Assert.True(actual.ManualBlendAbandonedFade);
        Assert.True(actual.InvalidControlsKeptMix);
    }
}
