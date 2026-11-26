using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Serves authenticated track tag snapshots and accepts edits for the selected library.</summary>
/// <remarks>2026-11-24: Bind the HTTPS tag read and write for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal static class TrackTagEndpoints
{
    private const string MissingTrack = "library-track-unavailable";

    /// <summary>Maps the track-specific query and command.</summary>
    public static void MapTrackTags(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(TrackTagRoutes.Pattern, GetAsync);
        routes.MapPost(TrackTagRoutes.Pattern, EditAsync);
    }

    private static async Task<IResult> GetAsync(Guid libraryId, Guid trackId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostTrackTags tags, CancellationToken token)
    {
        const string operation = "track-tags-read";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        context.Response.Headers.CacheControl = "no-store";
        var result = await tags.ReadAsync(libraryId, trackId, caller!, token).ConfigureAwait(false);
        return result is HostOutcome<TrackTagsSnapshot>.Rejected { Code: MissingTrack } missing
            ? Results.NotFound(new { missing.Code }) : HostLibraryMutation.ToHttpResult(result, operation);
    }

    private static Task<IResult> EditAsync(Guid libraryId, Guid trackId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostTrackTags tags, CancellationToken token) => HostLibraryMutation.RunAsync<EditTrackTagsRequest, TrackTagEditReceipt>(libraryId,
        context, verifier, (id, caller, request, ct) => tags.AcceptAsync(id, trackId, caller, request, ct), "track-tag-edit", token, MissingTrack);
}
