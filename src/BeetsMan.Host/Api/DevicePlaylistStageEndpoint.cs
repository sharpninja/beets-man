using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Exposes saved playlist staging through a library-scoped HTTPS command.</summary>
/// <remarks>2026-11-24: Bind the selected Android app and device staging plan for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public static class DevicePlaylistStageEndpoint
{
    /// <summary>Registers the version 1 staging endpoint.</summary>
    public static RouteHandlerBuilder MapDevicePlaylistStage(this IEndpointRouteBuilder routes) =>
        routes.MapPost(DevicePlaylistStageRoute.Pattern, StageAsync);

    private static Task<IResult> StageAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDevicePlaylistStager stager, CancellationToken token) => HostLibraryMutation.RunAsync<StageDevicePlaylistRequest, StageDevicePlaylistResponse>(
            libraryId, context, verifier, stager.StageAsync, "export-stage", token);
}
