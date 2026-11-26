using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Provides authenticated access to one provisioned native beets library.</summary>
/// <remarks>2026-11-25: Expose the approved STB08 state and initialization for FR-BEETS-001#ac-open.</remarks>
internal static class BeetsLibrarySetupEndpoints
{
    /// <summary>Maps the native library read and explicit initialization routes.</summary>
    public static void MapBeetsLibrarySetup(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(BeetsLibrarySetupRoutes.StatePattern, ReadStateAsync);
        routes.MapGet(BeetsLibrarySetupRoutes.ConfigurationPattern, ReadConfigurationAsync);
        routes.MapPost(BeetsLibrarySetupRoutes.StatePattern, InitializeAsync);
        routes.MapPost(BeetsLibrarySetupRoutes.ConfigurationPattern, SaveConfigurationAsync);
    }

    private static Task<IResult> ReadStateAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostBeetsLibrarySetup library, CancellationToken token) => ReadAsync<HostBeetsLibraryState>(libraryId, context, verifier, library.ReadAsync, token);

    private static Task<IResult> ReadConfigurationAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostBeetsLibrarySetup library, CancellationToken token) =>
        ReadAsync<HostBeetsConfiguration>(libraryId, context, verifier, library.ReadConfigurationAsync, token);

    private static Task<IResult> InitializeAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostBeetsLibrarySetup library, CancellationToken token) => HostLibraryMutation.RunAsync<InitializeHostBeetsRequest, HostBeetsLibraryState>(
        libraryId, context, verifier, library.InitializeAsync, "beets-library-initialize", token);

    /// <summary>Writes the supported native configuration for FR-BEETS-001#ac-open.</summary>
    /// <remarks>2026-11-25: Check the credential and origin through the existing HTTPS boundary.</remarks>
    private static Task<IResult> SaveConfigurationAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostBeetsLibrarySetup library, CancellationToken token) => HostLibraryMutation.RunAsync<SaveHostBeetsConfigurationRequest, HostBeetsConfiguration>(
        libraryId, context, verifier, library.SaveConfigurationAsync, "beets-library-configuration", token);

    private static async Task<IResult> ReadAsync<T>(Guid library, HttpContext context, IHostAccessTokenVerifier verifier,
        Func<Guid, AuthenticatedHostInstance, CancellationToken, Task<HostOutcome<T>>> read, CancellationToken token)
    {
        const string operation = "beets-library";
        context.Response.Headers.CacheControl = "no-store";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(library, context, verifier, operation, token).ConfigureAwait(false);
        return failure ?? HostLibraryMutation.ToHttpResult(await read(library, caller!, token).ConfigureAwait(false), operation);
    }
}
