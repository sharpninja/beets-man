using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves expected and deliberately incorrect plugin visualization observations.</summary>
/// <remarks>2026-11-25: Verify the independent assertions before the FR-PLUGINS-001#ac-visualize and ac-recovery implementation.</remarks>
public sealed class VisualizationWorkerMockTests
{
    [Fact]
    public void RejectsMisroutingStalenessAndPlaybackInterference()
    {
        var current = new VisualizationWorkerAssertions.Observation("waveform", 4, "example.visualizer/1.0.0/levels", 9,
            [0.125f, 0.5f, 0.875f], false, "plugin-worker-timeout", true);
        VisualizationWorkerAssertions.Verify(current);
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { Plugin = "example.visualizer/2.0.0/levels" }));
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { PluginSequence = 4 }));
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { PluginValues = [0.125f, 0.5f] }));
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { StaleWasProjected = true }));
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { DetachedReason = "unavailable" }));
        Assert.ThrowsAny<Exception>(() => VisualizationWorkerAssertions.Verify(current with { TransportResponsive = false }));
    }
}
