using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the observable two-deck fade trajectory and its interruption rules.</summary>
/// <remarks>2026-11-25: Verify FR-PLAYER-001#ac-fade through explicit audio-clock observations.</remarks>
internal static class AutomaticFadeAssertions
{
    internal sealed record Observation(bool DidNotStartEarly, bool IncomingReadyBeforeBlend, double Midpoint, bool PausedBoth,
        bool PromotedExactOccurrence, bool SoughtAndStoppedIncoming, bool SkippedAndStoppedIncoming, bool ManualTookOver,
        bool IncomingFailurePreservedOutgoing, bool ClampedToRemaining);

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.DidNotStartEarly);
        Assert.True(actual.IncomingReadyBeforeBlend);
        Assert.Equal(0.5, actual.Midpoint, 3);
        Assert.True(actual.PausedBoth);
        Assert.True(actual.PromotedExactOccurrence);
        Assert.True(actual.SoughtAndStoppedIncoming);
        Assert.True(actual.SkippedAndStoppedIncoming);
        Assert.True(actual.ManualTookOver);
        Assert.True(actual.IncomingFailurePreservedOutgoing);
        Assert.True(actual.ClampedToRemaining);
    }
}
