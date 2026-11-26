using System.Collections.Immutable;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Contains normalized waveform samples and spectrum levels from one output window.</summary>
/// <remarks>2026-11-25: Provide bounded telemetry for FR-PLAYER-001#ac-modes and FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record PlaybackVisualizationFrame(long Sequence, long CapturedAt, int SampleRate,
    ImmutableArray<float> Waveform, ImmutableArray<float> Spectrum)
{
    /// <summary>The maximum time a captured output window can be used.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMilliseconds(500);

    /// <summary>The number of time-domain values per output window.</summary>
    public const int WaveformPoints = 64;

    /// <summary>The number of frequency bands per output window.</summary>
    public const int SpectrumBands = 32;

    /// <summary>The number of mono samples used in one analysis window.</summary>
    public const int WindowSamples = 1024;
}

/// <summary>Identifies the live playback context independently of clock and queue-list revisions.</summary>
/// <remarks>2026-11-25: Invalidate superseded frames under FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record VisualizationPlaybackContext(bool Supported, Guid? QueueEntry, Guid? LeftTrack, Guid? RightTrack, long Discontinuity)
{
    /// <summary>Indicates whether at least one deck is producing playback.</summary>
    public bool Playing => Discontinuity % 2 == 0 && (LeftTrack is not null || RightTrack is not null);
}

/// <summary>Reads the newest bounded output window without waiting for the sound device.</summary>
/// <remarks>2026-11-25: Keep the optional visualization feed independent of transport for FR-PLAYER-001#ac-modes.</remarks>
public interface IPlaybackVisualizationSource
{
    /// <summary>Returns a fresh frame whose sequence is greater than the last consumed sequence, when available.</summary>
    PlaybackVisualizationFrame? ReadLatest(long afterSequence);
}
