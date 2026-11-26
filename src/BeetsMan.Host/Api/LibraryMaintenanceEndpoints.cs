using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Maps the authenticated preview and exact confirmation of a library maintenance operation.</summary>
/// <remarks>2026-11-24: Bind STB18's read-only preview, selected mode, and job receipt for FR-BEETS-001#ac-edit.</remarks>
internal static class LibraryMaintenanceEndpoints
{
    /// <summary>Registers the typed maintenance endpoints under the registered library.</summary>
    public static void MapLibraryMaintenance(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(LibraryMaintenanceRoutes.PreviewPattern, PreviewAsync);
        routes.MapPost(LibraryMaintenanceRoutes.ConfirmPattern, ConfirmAsync);
    }

    private static Task<IResult> PreviewAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier, HostLibraryMaintenance maintenance,
        CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return HostLibraryMutation.RunAsync<LibraryMaintenancePreviewRequest, LibraryMaintenancePlan>(libraryId, context, verifier,
            maintenance.PreviewAsync, "maintenance-preview", token, "library-track-unavailable");
    }

    private static Task<IResult> ConfirmAsync(Guid libraryId, Guid planId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostLibraryMaintenance maintenance, CancellationToken token) => HostLibraryMutation.RunAsync<ConfirmLibraryMaintenanceRequest, LibraryMaintenanceReceipt>(
            libraryId, context, verifier, (id, caller, request, ct) => maintenance.ConfirmAsync(id, planId, caller, request, ct),
            "maintenance-confirm", token, "maintenance-plan-unavailable");
}
