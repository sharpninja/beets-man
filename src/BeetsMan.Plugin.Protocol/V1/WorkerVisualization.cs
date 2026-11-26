using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Plugin.Protocol.V1;

/// <summary>Delivers one bounded mixed audio window to a negotiated visualization.</summary>
/// <remarks>2026-11-25: Define the correlated telemetry exchange for TR-PLUGINS-ARCH-001.</remarks>
/// <remarks>2026-11-26: Correlate bounded playback telemetry and its worker projection for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record WorkerVisualization(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contributionId")] string ContributionId,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("sampleRate")] int SampleRate,
    [property: JsonPropertyName("waveform")] ImmutableArray<float> Waveform,
    [property: JsonPropertyName("spectrum")] ImmutableArray<float> Spectrum);

/// <summary>Returns normalized levels for the host's visualization surface.</summary>
public sealed record WorkerVisualizationResult(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contributionId")] string ContributionId,
    [property: JsonPropertyName("sessionId")] Guid SessionId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("levels")] ImmutableArray<float> Levels);
