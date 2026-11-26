using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Runs beets library setup over the configured private HTTPS endpoint.</summary>
/// <remarks>2026-11-25: Validate the library and exact setup operation before projecting STB08 results for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Publish the reviewed configuration and check the exact write operation for FR-BEETS-001#ac-open.</remarks>
internal sealed class HttpBeetsLibrarySetup(HttpClient client, Uri host, Guid library, Guid origin, IRegisteredHostTokenProvider credentials)
    : IHostLibrarySetup
{
    private readonly Uri libraryState = RegisteredHostEndpoint.Resolve(host, library, BeetsLibrarySetupRoutes.StatePath);
    private readonly Uri configuration = RegisteredHostEndpoint.Resolve(host, library, BeetsLibrarySetupRoutes.ConfigurationPath);

    /// <inheritdoc />
    public Task<Outcome<HostLibraryState>> ReadAsync(CancellationToken token) =>
        RegisteredHostEndpoint.SendAsync<HostBeetsLibraryState, HostLibraryState>(client, credentials, library,
            new(HttpMethod.Get, libraryState), origin, "beets-library", "beets-library-scope-mismatch",
            reply => reply.OperationId is null ? Project(reply) : null, token);

    /// <inheritdoc />
    public Task<Outcome<HostLibraryConfiguration>> ReadConfigurationAsync(CancellationToken token) =>
        RegisteredHostEndpoint.SendAsync<HostBeetsConfiguration, HostLibraryConfiguration>(client, credentials, library,
            new(HttpMethod.Get, configuration), origin, "beets-library-configuration", "beets-library-scope-mismatch",
            reply => reply.OperationId is null ? ProjectConfiguration(reply) : null, token);

    /// <inheritdoc />
    public Task<Outcome<HostLibraryState>> InitializeAsync(HostLibrarySettings settings, long expectedRevision, Guid operation, CancellationToken token)
    {
        var request = new InitializeHostBeetsRequest(origin, operation, expectedRevision, ToWire(settings));
        return RegisteredHostEndpoint.SendAsync<HostBeetsLibraryState, HostLibraryState>(client, credentials, library,
            new(HttpMethod.Post, libraryState) { Content = JsonContent.Create(request) }, origin, "beets-library-initialize",
            "beets-library-initialization-receipt-mismatch", reply => reply.OperationId == operation && reply.IsInitialized && reply.CanEdit &&
                reply.Revision > expectedRevision ? Project(reply) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<HostLibraryConfiguration>> SaveConfigurationAsync(HostLibrarySettings settings, long expectedRevision, Guid operation,
        CancellationToken token)
    {
        var request = new SaveHostBeetsConfigurationRequest(origin, operation, expectedRevision, ToWire(settings));
        return RegisteredHostEndpoint.SendAsync<HostBeetsConfiguration, HostLibraryConfiguration>(client, credentials, library,
            new(HttpMethod.Post, configuration) { Content = JsonContent.Create(request) }, origin, "beets-library-configuration",
            "beets-library-configuration-receipt-mismatch", reply => reply.OperationId == operation && reply.CanEdit && reply.Revision > expectedRevision &&
                reply.Settings is { } saved && saved.CatalogPath == settings.CatalogPath && saved.SourceMusicRoot == settings.SourceMusicRoot &&
                saved.ConfigurationPath == settings.ConfigurationPath && saved.NamingTemplate == settings.NamingTemplate &&
                saved.ImportDefaults is { } defaults && defaults.Count == settings.ImportDefaults.Count &&
                settings.ImportDefaults.All(item => defaults.TryGetValue(item.Key, out var value) && item.Value == value) &&
                !saved.EnabledPluginIds.IsDefault && !settings.EnabledPluginIds.IsDefault &&
                saved.EnabledPluginIds.SequenceEqual(settings.EnabledPluginIds.Order(StringComparer.Ordinal))
                    ? ProjectConfiguration(reply) : null, token);
    }

    private HostLibraryConfiguration? ProjectConfiguration(HostBeetsConfiguration reply) =>
        reply.LibraryId == library && reply.Revision >= 0 && !reply.AvailablePluginIds.IsDefault && reply.Settings is
            { CatalogPath.Length: > 0, SourceMusicRoot.Length: > 0, ConfigurationPath.Length: > 0, ImportDefaults: not null,
                EnabledPluginIds.IsDefault: false, NamingTemplate: not null } current
            ? new(library, reply.Revision, new(current.CatalogPath, current.SourceMusicRoot, current.NamingTemplate,
                current.ImportDefaults, current.EnabledPluginIds, current.ConfigurationPath), reply.CanEdit)
                { AvailablePluginIds = reply.AvailablePluginIds } : null;

    private HostLibraryState? Project(HostBeetsLibraryState reply) =>
        reply.LibraryId == library && reply.Revision >= 0 ? new(library, reply.Revision, reply.IsInitialized, true, reply.CanEdit) : null;

    private static HostBeetsSettings ToWire(HostLibrarySettings settings) => new(settings.CatalogPath, settings.SourceMusicRoot,
        settings.NamingTemplate, settings.ImportDefaults, settings.EnabledPluginIds, settings.ConfigurationPath!);
}
