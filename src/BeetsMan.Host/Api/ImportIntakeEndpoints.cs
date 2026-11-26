using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Serves the authenticated guided import copy and job checkpoints.</summary>
/// <remarks>2026-11-24: Implement the STB08 upload boundary for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Authorize album review approval for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal static class ImportIntakeEndpoints
{
    /// <summary>Registers the upload, stream, acceptance, and job lookup routes.</summary>
    public static void MapImportIntake(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(ImportIntakeRoutes.BeginPattern, BeginAsync);
        routes.MapPut(ImportIntakeRoutes.PartPattern, CopyAsync);
        routes.MapPost(ImportIntakeRoutes.StartPattern, StartAsync);
        routes.MapGet(ImportIntakeRoutes.JobPattern, GetJobAsync);
        routes.MapPost(ImportIntakeRoutes.ChoicePattern, ChooseAsync);
        routes.MapPost(ImportIntakeRoutes.RetryPattern, RetryAsync);
    }

    private static Task<IResult> BeginAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier, HostImportIntake intake,
        CancellationToken token) => HostLibraryMutation.RunAsync<BeginImportUploadRequest, ImportUploadReceipt>(libraryId, context, verifier,
        intake.BeginAsync, "import-upload", token);

    private static async Task<IResult> CopyAsync(Guid libraryId, Guid uploadId, int index, HttpContext context, IHostAccessTokenVerifier verifier,
        HostImportIntake intake, CancellationToken token)
    {
        const string operation = "import-copy";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        var result = await intake.PutPartAsync(libraryId, caller!, uploadId, index, context.Request.ContentLength, context.Request.Body,
            size =>
            {
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maximum)
                    maximum.MaxRequestBodySize = size;
            }, token).ConfigureAwait(false);
        return result is HostOutcome<ImportUploadPartReceipt>.Rejected { Code: "import-upload-unavailable" } missing
            ? Results.NotFound(new { missing.Code }) : HostLibraryMutation.ToHttpResult(result, operation);
    }

    private static Task<IResult> StartAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier, HostImportIntake intake,
        CancellationToken token) => HostLibraryMutation.RunAsync<StartImportRequest, StartImportReceipt>(libraryId, context, verifier,
        intake.StartAsync, "import-start", token);

    private static Task<IResult> ChooseAsync(Guid libraryId, Guid jobId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostImportReview review, CancellationToken token) => HostLibraryMutation.RunAsync<ChooseImportRequest, ChooseImportReceipt>(
        libraryId, context, verifier, (id, caller, request, ct) => review.ChooseAsync(id, jobId, caller, request, ct),
        "import-choice", token, notFoundCode: "library-job-unavailable");

    private static Task<IResult> RetryAsync(Guid libraryId, Guid jobId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostImportIntake intake, CancellationToken token) => HostLibraryMutation.RunAsync<RetryImportRequest, RetryImportReceipt>(
        libraryId, context, verifier, (id, caller, request, ct) => intake.RetryAsync(id, jobId, caller, request, ct),
        "import-retry", token, notFoundCode: "library-job-unavailable");

    private static async Task<IResult> GetJobAsync(Guid libraryId, Guid jobId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostImportIntake intake, CancellationToken token)
    {
        const string operation = "library-job-read";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        context.Response.Headers.CacheControl = "no-store";
        var result = await intake.ReadJobAsync(libraryId, caller!, jobId, token).ConfigureAwait(false);
        return result is HostOutcome<ImportJobSnapshot>.Rejected { Code: "library-job-unavailable" } missing
            ? Results.NotFound(new { missing.Code }) : HostLibraryMutation.ToHttpResult(result, operation);
    }
}
