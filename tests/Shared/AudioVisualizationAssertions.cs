using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies bounded waveform and spectral observations from the same audio window.</summary>
/// <remarks>2026-11-25: Verify FR-PLAYER-001#ac-modes and FR-PLUGINS-001#ac-visualize.</remarks>
internal static class AudioVisualizationAssertions
{
    internal sealed record Observation(long Sequence, IReadOnlyList<float> Waveform, IReadOnlyList<float> Spectrum);

    /// <summary>Checks a 1,500 Hz tone at 48 kHz alongside initial, duplicate and expired reads.</summary>
    internal static void Verify(Observation? before, Observation? live, Observation? duplicate, Observation? expired)
    {
        Assert.Null(before);
        var frame = Assert.IsType<Observation>(live);
        Assert.True(frame.Sequence > 0);
        Assert.Equal(64, frame.Waveform.Count);
        Assert.Equal(32, frame.Spectrum.Count);
        Assert.All(frame.Waveform, point => Assert.InRange(point, -1, 1));
        Assert.All(frame.Spectrum, band => Assert.InRange(band, 0, 1));
        Assert.Contains(frame.Waveform, point => point > 0.2f);
        Assert.Contains(frame.Waveform, point => point < -0.2f);
        Assert.InRange(frame.Spectrum[17], 0.20f, 0.85f);
        Assert.True(frame.Spectrum[17] > frame.Spectrum[8] * 8);
        Assert.True(frame.Spectrum[17] > frame.Spectrum[26] * 8);
        Assert.Null(duplicate);
        Assert.Null(expired);
    }
}
