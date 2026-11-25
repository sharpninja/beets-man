using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Exposes registered media status and designated mirror commands through private HTTPS.</summary>
/// <remarks>2026-11-24: Map STB10 to the library-host boundary for FR-FILESYNC-001#ac-mirror.</remarks>
internal static class MediaMirrorEndpoints
{
    /// <summary>Registers the status query, peer-scoped pairing, and mirror control endpoints.</summary>
    public static void MapMediaMirrors(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(MediaMirrorRoutes.ReadPattern, ReadAsync);
        routes.MapPost(MediaMirrorRoutes.ControlPattern, ControlAsync);
        routes.MapPost(MediaMirrorRoutes.PairPattern, PairAsync);
    }

    private static async Task<IResult> ReadAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostPcMirrors mirrors, CancellationToken token)
    {
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, "media-mirror-read", token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        var state = await mirrors.ReadAsync(libraryId, caller!, token).ConfigureAwait(false);
        return HostLibraryMutation.ToHttpResult(state, "media-mirror-read");
    }

    private static Task<IResult> ControlAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostPcMirrors mirrors, CancellationToken token) => HostLibraryMutation.RunAsync<MediaMirrorControlRequest, MediaMirrorControlResponse>(
        libraryId, context, verifier, mirrors.ControlAsync, "media-mirror-control", token);

    private static Task<IResult> PairAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostPcMirrors mirrors, CancellationToken token) => HostLibraryMutation.RunAsync<MediaPairingRequest, MediaPairingResponse>(
        libraryId, context, verifier, mirrors.PairAsync, "media-pairing", token);
}
