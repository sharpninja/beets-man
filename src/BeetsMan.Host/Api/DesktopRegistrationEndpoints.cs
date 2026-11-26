using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Maps the private desktop registration routes through the existing HTTPS credential verifier.</summary>
/// <remarks>2026-11-25: Expose typed desktop registration and revocation for FR-RECORDS-001#ac-register.</remarks>
internal static class DesktopRegistrationEndpoints
{
    /// <summary>Registers the roster, enrollment and revocation endpoints.</summary>
    public static void MapDesktopRegistration(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(DesktopRegistrationRoutes.DirectoryPattern, ReadAsync);
        routes.MapPost(DesktopRegistrationRoutes.DirectoryPattern, RegisterAsync);
        routes.MapPost(DesktopRegistrationRoutes.RevokePattern, RevokeAsync);
    }

    private static async Task<IResult> ReadAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDesktopRegistration registry, CancellationToken token)
    {
        const string operation = "desktop-registry";
        context.Response.Headers.CacheControl = "no-store";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        return failure ?? HostLibraryMutation.ToHttpResult(await registry.ReadAsync(libraryId, caller!, token).ConfigureAwait(false), operation);
    }

    private static Task<IResult> RegisterAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDesktopRegistration registry, CancellationToken token) => HostLibraryMutation.RunAsync<RegisterDesktopRequest, RegisterDesktopResponse>(
        libraryId, context, verifier, registry.RegisterAsync, "desktop-registration", token);

    private static Task<IResult> RevokeAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDesktopRegistration registry, CancellationToken token) => HostLibraryMutation.RunAsync<RevokeDesktopRequest, RevokeDesktopResponse>(
        libraryId, context, verifier, registry.RevokeAsync, "desktop-revocation", token);
}
