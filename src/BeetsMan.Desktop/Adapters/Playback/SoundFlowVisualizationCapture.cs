using System.Collections.Immutable;
using System.Numerics;
using BeetsMan.Desktop.Application.Playback;
using SoundFlow.Abstracts;
using SoundFlow.Structs;
using SoundFlow.Utils;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Captures one rolling master-mix window for analysis off the SoundFlow render callback.</summary>
/// <remarks>2026-11-25: Bind FR-PLAYER-001#ac-modes and FR-PLUGINS-001#ac-visualize to the native output signal.</remarks>
public sealed class SoundFlowVisualizationCapture(AudioFormat format, TimeProvider? clock = null)
    : AudioAnalyzer(format), IPlaybackVisualizationSource
{
    private const int WindowSize = PlaybackVisualizationFrame.WindowSamples;
    private static readonly double[] Window = Enumerable.Range(0, WindowSize)
        .Select(index => 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / WindowSize)).ToArray();
    private readonly object gate = new();
    private readonly float[] samples = new float[WindowSize];
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int writeIndex;
    private int filled;
    private int gap;
    private long sequence;
    private long capturedAt;

    /// <summary>Copies at most one window of normalized mono samples, skipping contention without blocking audio.</summary>
    protected override void Analyze(ReadOnlySpan<float> buffer, int channels)
    {
        if (channels <= 0 || buffer.Length < channels)
            return;
        if (!Monitor.TryEnter(gate))
        {
            Volatile.Write(ref gap, 1);
            return;
        }
        try
        {
            var timestamp = time.GetTimestamp();
            if (Interlocked.Exchange(ref gap, 0) != 0 ||
                filled != 0 && time.GetElapsedTime(capturedAt, timestamp) >= PlaybackVisualizationFrame.MaximumAge)
                filled = 0;
            var available = buffer.Length / channels;
            var start = Math.Max(0, available - WindowSize);
            for (var index = start; index < available; index++)
            {
                double sum = 0;
                var offset = index * channels;
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = buffer[offset + channel];
                    if (float.IsFinite(value))
                        sum += Math.Clamp(value, -1f, 1f);
                }
                samples[writeIndex] = (float)(sum / channels);
                writeIndex = (writeIndex + 1) % WindowSize;
            }
            filled = Math.Min(WindowSize, filled + available - start);
            capturedAt = timestamp;
            sequence++;
        }
        finally { Monitor.Exit(gate); }
    }

    /// <inheritdoc />
    public PlaybackVisualizationFrame? ReadLatest(long afterSequence)
    {
        Span<float> window = stackalloc float[WindowSize];
        long current;
        long timestamp;
        lock (gate)
        {
            if (Volatile.Read(ref gap) != 0 || filled != WindowSize || sequence <= afterSequence ||
                time.GetElapsedTime(capturedAt) >= PlaybackVisualizationFrame.MaximumAge)
                return null;
            current = sequence;
            timestamp = capturedAt;
            samples.AsSpan(writeIndex).CopyTo(window);
            samples.AsSpan(0, writeIndex).CopyTo(window[(WindowSize - writeIndex)..]);
        }
        return new(current, timestamp, Format.SampleRate, BuildWaveform(window), BuildSpectrum(window));
    }

    private static ImmutableArray<float> BuildWaveform(ReadOnlySpan<float> window)
    {
        const int groupSize = WindowSize / PlaybackVisualizationFrame.WaveformPoints;
        var points = ImmutableArray.CreateBuilder<float>(PlaybackVisualizationFrame.WaveformPoints);
        for (var start = 0; start < WindowSize; start += groupSize)
        {
            var sum = 0f;
            for (var index = start; index < start + groupSize; index++)
                sum += window[index];
            points.Add(sum / groupSize);
        }
        return points.MoveToImmutable();
    }

    private static ImmutableArray<float> BuildSpectrum(ReadOnlySpan<float> window)
    {
        var input = new Complex[WindowSize];
        for (var index = 0; index < input.Length; index++)
            input[index] = window[index] * Window[index];
        MathHelper.Fft(input);
        var bands = new float[PlaybackVisualizationFrame.SpectrumBands];
        for (var bin = 1; bin < WindowSize / 2; bin++)
        {
            var band = (int)(bands.Length * Math.Log2(bin) / Math.Log2(WindowSize / 2));
            var amplitude = (float)Math.Min(1, 4 * input[bin].Magnitude / WindowSize);
            bands[band] = Math.Max(bands[band], amplitude);
        }
        return ImmutableArray.CreateRange(bands);
    }
}
