using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Maps the authenticated desktop inspection and retry routes for a queued export transfer.</summary>
/// <remarks>2026-11-24: Surface durable receiver progress for FR-FILESYNC-001#ac-safety.</remarks>
public static class DeviceExportTransferEndpoints
{
    private const string Inspect = "export-transfer-read";
    private const string Retry = "export-transfer-retry";

    /// <summary>Registers both version 1 transfer routes.</summary>
    public static void MapDeviceExportTransfers(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(DeviceExportTransferRoutes.ReadPattern, ReadAsync);
        routes.MapPost(DeviceExportTransferRoutes.RetryPattern, RetryAsync);
    }

    private static async Task<IResult> ReadAsync(Guid libraryId, Guid transferId, HttpContext context, IHostAccessTokenVerifier verifier,
        IDeviceExportTransferStore store, TimeProvider time, CancellationToken token)
    {
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, Inspect, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        var result = await store.ReadTransferAsync(libraryId, caller!, transferId, time.GetUtcNow(), token).ConfigureAwait(false);
        return HostLibraryMutation.ToHttpResult(result, Inspect);
    }

    private static Task<IResult> RetryAsync(Guid libraryId, Guid transferId, HttpContext context, IHostAccessTokenVerifier verifier,
        IDeviceExportTransferStore store, TimeProvider time, CancellationToken token) =>
        HostLibraryMutation.RunAsync<RetryDeviceExportTransferRequest, RetryDeviceExportTransferResponse>(libraryId, context, verifier,
            (scope, caller, request, ct) => store.RetryTransferAsync(scope, caller, transferId, request, time.GetUtcNow(), ct), Retry, token);
}
