using Xunit;
using Observation = BeetsMan.Desktop.Tests.AudioVisualizationAssertions.Observation;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the visualization assertions detect an incorrect or stale render input.</summary>
/// <remarks>2026-11-25: Independent controls for FR-PLAYER-001#ac-modes and FR-PLUGINS-001#ac-visualize.</remarks>
public sealed class AudioVisualizationMockTests
{
    [Fact]
    public void ExpectedSignalAndIndependentNegativeControls()
    {
        var waveform = Enumerable.Range(0, 64).Select(index => index % 2 == 0 ? 0.31f : -0.31f).ToArray();
        var spectrum = new float[32];
        spectrum[17] = 0.5f;
        var frame = new Observation(1, waveform, spectrum);
        AudioVisualizationAssertions.Verify(null, frame, null, null);
        Assert.ThrowsAny<Exception>(() => AudioVisualizationAssertions.Verify(null, frame with { Waveform = [0, 1] }, null, null));
        Assert.ThrowsAny<Exception>(() => AudioVisualizationAssertions.Verify(null, frame with { Spectrum = new float[32] }, null, null));
        Assert.ThrowsAny<Exception>(() => AudioVisualizationAssertions.Verify(null, frame, frame, null));
        Assert.ThrowsAny<Exception>(() => AudioVisualizationAssertions.Verify(null, frame, null, frame));
    }
}
