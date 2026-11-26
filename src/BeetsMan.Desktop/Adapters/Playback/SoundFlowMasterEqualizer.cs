using System.Collections.Immutable;
using SoundFlow.Abstracts;
using SoundFlow.Enums;
using SoundFlow.Modifiers;
using SoundFlow.Structs;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Swaps a prepared SoundFlow EQ on the master mixer at an audio-buffer boundary.</summary>
/// <remarks>2026-11-25: Keep filter setup out of the render callback and support bypass for FR-PLAYER-001#ac-mix and the STB04 master EQ.</remarks>
internal sealed class SoundFlowMasterEqualizer(AudioFormat format) : SoundModifier
{
    private static readonly float[] Centers = [31.25f, 62.5f, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    private const float OctaveBandQuality = 1.41421356f;
    private ParametricEqualizer? current;

    /// <summary>Prepares the new bank before publishing it to the render callback.</summary>
    internal void Configure(ImmutableArray<double> gains, bool bypass)
    {
        ParametricEqualizer? next = null;
        if (!bypass)
        {
            var bands = new List<EqualizerBand>(Centers.Length);
            for (var index = 0; index < Centers.Length; index++)
            {
                if (gains[index] != 0 && Centers[index] * 2 < format.SampleRate)
                    bands.Add(new(FilterType.Peaking, Centers[index], (float)gains[index], OctaveBandQuality, 1));
            }
            if (bands.Count != 0)
            {
                next = new(format);
                next.AddBands(bands);
            }
        }
        Volatile.Write(ref current, next);
    }

    /// <inheritdoc />
    public override void Process(Span<float> buffer, int channels) => Volatile.Read(ref current)?.Process(buffer, channels);

    /// <inheritdoc />
    public override float ProcessSample(float sample, int channel) => Volatile.Read(ref current)?.ProcessSample(sample, channel) ?? sample;
}
