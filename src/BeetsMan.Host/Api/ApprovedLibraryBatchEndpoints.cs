using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Registers submission and status for an approved, scoped library batch.</summary>
/// <remarks>2026-11-24: Authorize whole-batch publication for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Require the approved manifest before reading a body for FR-LIBRARY-002#ac-batch-local and ac-batch-commit.</remarks>
internal static class ApprovedLibraryBatchEndpoints
{
    public static void MapApprovedLibraryBatches(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(ApprovedLibraryBatchRoutes.ApprovePattern, SubmitAsync);
        routes.MapGet(ApprovedLibraryBatchRoutes.StatusPattern, InspectAsync);
        routes.MapPost(ApprovedLibraryBatchRoutes.WithdrawPattern, WithdrawAsync);
        routes.MapPut(ApprovedLibraryBatchRoutes.ImportPartPattern, CopyAsync);
        routes.MapPost(ApprovedLibraryBatchRoutes.ImportChoicePattern, ChooseImportAsync);
    }

    private static Task<IResult> SubmitAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostApprovedLibraryBatches service, CancellationToken token) => HostLibraryMutation.RunAsync<ApproveLibraryBatchRequest, ApprovedLibraryBatchStatus>(
        libraryId, context, verifier, service.SubmitAsync, "library-batch-submit", token);

    /// <summary>Withdraws a pre-write approval under its registered owner and displayed review.</summary>
    /// <remarks>2026-11-25: Authenticate withdrawal for FR-LIBRARY-002#ac-batch-revert.</remarks>
    private static Task<IResult> WithdrawAsync(Guid libraryId, Guid operationId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostApprovedLibraryBatches service, CancellationToken token) => HostLibraryMutation.RunAsync<WithdrawApprovedLibraryBatchRequest, WithdrawApprovedLibraryBatchReceipt>(
        libraryId, context, verifier, (library, caller, request, ct) => service.WithdrawAsync(library, caller, operationId, request, ct), "library-batch-withdraw", token);

    private static async Task<IResult> InspectAsync(Guid libraryId, Guid operationId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostApprovedLibraryBatches service, CancellationToken token)
    {
        const string operation = "library-batch-status";
        var (caller, failed) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failed is not null)
            return failed;
        context.Response.Headers.CacheControl = "no-store";
        var result = await service.InspectAsync(libraryId, caller!, operationId, token).ConfigureAwait(false);
        return result is HostOutcome<ApprovedLibraryBatchStatus>.Rejected { Code: "library-batch-unavailable" } missing
            ? Results.NotFound(new { missing.Code }) : HostLibraryMutation.ToHttpResult(result, operation);
    }

    /// <summary>Receives a verified copy only for the authenticated batch owner.</summary>
    private static async Task<IResult> CopyAsync(Guid libraryId, Guid operationId, int position, int index, Guid fileId, HttpContext context,
        IHostAccessTokenVerifier verifier, HostApprovedBatchImports service, CancellationToken token)
    {
        const string operation = "library-batch-import";
        var (caller, failed) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failed is not null)
            return failed;
        var result = await service.PutAsync(libraryId, caller!, operationId, position, index, fileId, context.Request.ContentLength,
            context.Request.Body, size =>
            {
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maximum)
                    maximum.MaxRequestBodySize = size;
            }, token).ConfigureAwait(false);
        return result is HostOutcome<ApprovedBatchImportPartReceipt>.Rejected { Code: "batch-import-part-unavailable" } missing
            ? Results.NotFound(new { missing.Code }) : HostLibraryMutation.ToHttpResult(result, operation);
    }

    /// <summary>Records a scoped guided choice before the batch can begin native work.</summary>
    private static Task<IResult> ChooseImportAsync(Guid libraryId, Guid operationId, int position, HttpContext context, IHostAccessTokenVerifier verifier,
        HostApprovedBatchImportReviews service, CancellationToken token) => HostLibraryMutation.RunAsync<ChooseApprovedBatchImportRequest, ApprovedBatchImportChoiceReceipt>(
        libraryId, context, verifier, (library, caller, request, ct) => service.ChooseAsync(library, caller, operationId, position, request, ct),
        "batch-import-choice", token, notFoundCode: "batch-import-review-unavailable");
}
