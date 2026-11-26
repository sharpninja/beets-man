using System.Text.Json.Serialization;

namespace BeetsMan.Plugin.Protocol.V1;

/// <summary>Starts protocol negotiation for one exact plugin version and request.</summary>
/// <remarks>2026-11-25: Define the IPC greeting for TR-PLUGINS-ARCH-001 and FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record WorkerHello(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version);

/// <summary>Identifies one package-declared contribution supported by the live worker.</summary>
public sealed record WorkerContribution(
    [property: JsonPropertyName("contributionId")] string ContributionId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("telemetryProtocol")] int? TelemetryProtocol = null);

/// <summary>Confirms the greeting and returns the worker's available contribution subset.</summary>
public sealed record WorkerReady(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contributions")] WorkerContribution[] Contributions);
