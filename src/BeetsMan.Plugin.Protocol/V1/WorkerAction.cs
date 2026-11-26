using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Plugin.Protocol.V1;

/// <summary>Invokes a negotiated contribution with plain bounded input values.</summary>
/// <remarks>2026-11-25: Define correlated contribution execution for TR-PLUGINS-ARCH-001.</remarks>
/// <remarks>2026-11-26: Correlate approved application commands with worker actions for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record WorkerAction(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contributionId")] string ContributionId,
    [property: JsonPropertyName("arguments")] ImmutableDictionary<string, string> Arguments);

/// <summary>Returns one correlated result and an optional request for an installed host action grant.</summary>
public sealed record WorkerActionResult(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("pluginId")] string PluginId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("contributionId")] string ContributionId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hostAction")] string? HostAction = null);
