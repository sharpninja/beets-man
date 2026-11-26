using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Exposes the host-managed settings for opening and initializing one beets library.</summary>
/// <remarks>2026-11-25: Keep the YAML and catalog paths distinct for STB08 and FR-BEETS-001#ac-open.</remarks>
public sealed record HostBeetsSettings(string CatalogPath, string SourceMusicRoot, string NamingTemplate,
    ImmutableDictionary<string, string> ImportDefaults, ImmutableArray<string> EnabledPluginIds, string ConfigurationPath);

/// <summary>Reports the current known catalog state and whether the caller can manage it.</summary>
public sealed record HostBeetsLibraryState(Guid LibraryId, long Revision, bool IsInitialized, bool CanEdit, Guid? OperationId = null);

/// <summary>Returns the provisioned native settings and the revision used for an initialization decision.</summary>
/// <remarks>2026-11-25: Carry the local plugin choices and an exact settings write receipt for FR-BEETS-001#ac-open.</remarks>
public sealed record HostBeetsConfiguration(Guid LibraryId, long Revision, HostBeetsSettings Settings, bool CanEdit)
{
    public ImmutableArray<string> AvailablePluginIds { get; init; } = [];
    public Guid? OperationId { get; init; }
}

/// <summary>Applies a supported configuration at the observed host revision.</summary>
/// <remarks>2026-11-25: Keep the request scope and stable operation for FR-BEETS-001#ac-open.</remarks>
public sealed record SaveHostBeetsConfigurationRequest(Guid OriginInstanceId, Guid OperationId,
    [property: JsonRequired] long ExpectedRevision, HostBeetsSettings Settings);

/// <summary>Approves creation of the provisioned native catalog and YAML at a reviewed revision.</summary>
public sealed record InitializeHostBeetsRequest(Guid OriginInstanceId, Guid OperationId,
    [property: JsonRequired] long ExpectedRevision, HostBeetsSettings Settings);

/// <summary>Addresses the private beets library setup routes.</summary>
/// <remarks>2026-11-25: Publish the STB08 state, configuration, and native initialization routes for FR-BEETS-001#ac-open.</remarks>
public static class BeetsLibrarySetupRoutes
{
    public const string StatePattern = "/api/v1/libraries/{libraryId:guid}/beets-library";
    public const string ConfigurationPattern = StatePattern + "/configuration";

    /// <summary>Addresses the selected host library's state and initialization.</summary>
    public static string StatePath(Guid library) => $"api/v1/libraries/{library:D}/beets-library";

    /// <summary>Addresses the selected host library's provisioned beets settings.</summary>
    public static string ConfigurationPath(Guid library) => StatePath(library) + "/configuration";
}
