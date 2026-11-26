using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves that the fade assertions reject incorrect timing, unsafe transitions and invalid ownership.</summary>
/// <remarks>2026-11-25: Exercise independent negative controls before implementing FR-PLAYER-001#ac-fade.</remarks>
public sealed class AutomaticFadeMockTests
{
    [Fact]
    public void FadeAssertionsRejectWrongAudioClockTrajectories()
    {
        var fixture = new AutomaticFadeAssertions.Observation(true, true, 0.5, true, true, true, true, true, true, true);
        AutomaticFadeAssertions.Verify(fixture);
        AutomaticFadeAssertions.Observation[] wrong =
        [
            fixture with { DidNotStartEarly = false }, fixture with { IncomingReadyBeforeBlend = false }, fixture with { Midpoint = 0.75 },
            fixture with { PausedBoth = false }, fixture with { PromotedExactOccurrence = false }, fixture with { SoughtAndStoppedIncoming = false },
            fixture with { SkippedAndStoppedIncoming = false }, fixture with { ManualTookOver = false },
            fixture with { IncomingFailurePreservedOutgoing = false }, fixture with { ClampedToRemaining = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => AutomaticFadeAssertions.Verify(observation)));
    }
}
