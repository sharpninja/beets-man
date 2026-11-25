using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Resolves bearer credentials to a verified library and instance without exposing the credential to the writer.</summary>
/// <remarks>2026-11-24: Bind the private host boundary for FR-RECORDS-001#ac-register.</remarks>
public interface IHostAccessTokenVerifier
{
    /// <summary>Returns the credential's registered identity, or no identity for an unknown token.</summary>
    ValueTask<AuthenticatedHostInstance?> VerifyAsync(string bearerToken, CancellationToken cancellationToken);
}

/// <summary>Maps HTTPS playlist publication requests to the transactional host writer.</summary>
/// <remarks>2026-11-24: Bind version 1 playlist saves for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public static class PlaylistPublicationEndpoint
{
    /// <summary>Registers the version 1 saved playlist command endpoint.</summary>
    public static RouteHandlerBuilder MapPlaylistPublication(this IEndpointRouteBuilder routes) =>
        routes.MapPost(PlaylistPublicationRoute.Pattern, PublishAsync);

    private static Task<IResult> PublishAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostPlaylistWriter writer, CancellationToken token) => HostLibraryMutation.RunAsync<PlaylistSaveRequest, PlaylistSaveResponse>(
            libraryId, context, verifier, writer.SaveAsync, "playlist", token);
}
