using System.Collections.Immutable;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Relays live Android connection observations through the authenticated library endpoint.</summary>
/// <remarks>2026-11-24: Keep STB21 event snapshots scoped to registered desktops for FR-FILESYNC-001#ac-arrival.</remarks>
internal static class AndroidArrivalEndpoint
{
    public static void MapAndroidArrivals(this IEndpointRouteBuilder routes) =>
        routes.MapGet(AndroidArrivalRoute.ReadPattern, ReadAsync);

    private static async Task<IResult> ReadAsync(Guid libraryId, Guid? epoch, long? after, HttpContext context,
        IHostAccessTokenVerifier verifier, LiteDbHostPlaylistStore store, AndroidArrivalHub hub, CancellationToken token)
    {
        const string operation = "android-arrival";
        context.Response.Headers.CacheControl = "no-store";
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        if (epoch.HasValue != after.HasValue || epoch == Guid.Empty || after is < 0)
            return HostLibraryMutation.ToHttpResult(new HostOutcome<AndroidArrivalSnapshot>.Rejected("android-arrival-cursor-invalid"), operation);
        var ready = await store.ReadAndroidArrivalsAsync(libraryId, caller, token).ConfigureAwait(false);
        if (ready is not HostOutcome<ImmutableArray<RegisteredAndroidExport>>.Success { Value: var authorized })
            return HostLibraryMutation.ToHttpResult(ready.PropagateFailure<AndroidArrivalSnapshot>(), operation);
        if (epoch is not null)
        {
            await hub.WaitAsync(epoch, after, token).ConfigureAwait(false);
            ready = await store.ReadAndroidArrivalsAsync(libraryId, caller, token).ConfigureAwait(false);
            if (ready is not HostOutcome<ImmutableArray<RegisteredAndroidExport>>.Success current)
                return HostLibraryMutation.ToHttpResult(ready.PropagateFailure<AndroidArrivalSnapshot>(), operation);
            authorized = current.Value;
        }
        return Results.Ok(hub.Read(epoch, after, authorized));
    }
}
