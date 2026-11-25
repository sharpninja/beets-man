using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Exposes read-only layout and staged-list endpoints for a registered Android device.</summary>
/// <remarks>2026-11-24: Map FR-FILESYNC-001#ac-layouts and ac-playlist-drop to the authorized host queries.</remarks>
public static class DeviceExportQueryEndpoints
{
    private const string Operation = "export-read";

    /// <summary>Registers both version 1 device export queries.</summary>
    public static void MapDeviceExportQueries(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(DeviceExportQueryRoutes.LayoutsPattern, ReadLayoutsAsync);
        routes.MapGet(DeviceExportQueryRoutes.PlanPattern, ReadPlanAsync);
    }

    private static async Task<IResult> ReadLayoutsAsync(Guid libraryId, Guid deviceId, HttpContext context,
        IHostAccessTokenVerifier verifier, HostDeviceExportReader reader, CancellationToken token)
    {
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, Operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        var result = await reader.ReadLayoutsAsync(libraryId, caller!, deviceId, token).ConfigureAwait(false);
        return HostLibraryMutation.ToHttpResult(result, Operation);
    }

    private static async Task<IResult> ReadPlanAsync(Guid libraryId, Guid deviceId, Guid? planId, string? profileId, long? profileRevision,
        HttpContext context, IHostAccessTokenVerifier verifier, HostDeviceExportReader reader, CancellationToken token)
    {
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, Operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        var result = await reader.ReadPlanAsync(libraryId, caller!, new(deviceId, planId, profileId, profileRevision), token).ConfigureAwait(false);
        return HostLibraryMutation.ToHttpResult(result, Operation);
    }
}
