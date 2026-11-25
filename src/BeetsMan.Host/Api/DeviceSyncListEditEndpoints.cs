using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Submits STB10 staged-list mutations through authenticated HTTPS.</summary>
/// <remarks>2026-11-24: Map direct audio and entry removal for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public static class DeviceSyncListEditEndpoints
{
    /// <summary>Registers both staged-list mutation endpoints.</summary>
    public static void MapDeviceSyncListEdits(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(DeviceSyncListEditRoutes.StageTracksPattern, StageTracksAsync);
        routes.MapPost(DeviceSyncListEditRoutes.RemovePattern, RemoveAsync);
    }

    private static Task<IResult> StageTracksAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDeviceSyncListEditor editor, CancellationToken token) => HostLibraryMutation.RunAsync<StageDeviceTracksRequest, DeviceSyncListEditResponse>(
            libraryId, context, verifier, editor.StageTracksAsync, "export-stage", token);

    private static Task<IResult> RemoveAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostDeviceSyncListEditor editor, CancellationToken token) => HostLibraryMutation.RunAsync<RemoveDeviceExportEntriesRequest, DeviceSyncListEditResponse>(
            libraryId, context, verifier, editor.RemoveAsync, "export-edit", token);
}
