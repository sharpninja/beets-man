using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Serves the scoped catalog revision feed and recipient notification ledger.</summary>
/// <remarks>2026-11-24: Authenticate replica reads and acknowledgements for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
internal static class CatalogReplicationEndpoints
{
    /// <summary>Registers the feed, pending notification, and acknowledgement routes.</summary>
    public static void MapCatalogReplication(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(CatalogReplicationRoutes.ChangesPattern, ChangesAsync);
        routes.MapGet(CatalogReplicationRoutes.NotificationsPattern, NotificationsAsync);
        routes.MapPost(CatalogReplicationRoutes.AcknowledgePattern, AcknowledgeAsync);
        routes.MapPost(CatalogSnapshotRoutes.BeginPattern, BeginSnapshotAsync);
        routes.MapGet(CatalogSnapshotRoutes.PagePattern, SnapshotPageAsync);
        routes.MapPost(LibraryRecordRoutes.PublishPattern, PublishRecordAsync);
    }

    /// <summary>Records a listening or default change from the authenticated origin.</summary>
    /// <remarks>2026-11-25: Admit shared records for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
    private static Task<IResult> PublishRecordAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        HostLibraryRecords records, CancellationToken token) => HostLibraryMutation.RunAsync<PublishLibraryRecordRequest, LibraryRecordReceipt>(
        libraryId, context, verifier, records.PublishAsync, "library-record", token);

    private static Task<IResult> ChangesAsync(Guid libraryId, long after, long? upper, int? limit, HttpContext context,
        IHostAccessTokenVerifier verifier, IHostCatalogReplication replica, CancellationToken token) => ReadAsync(libraryId, context, verifier,
        "catalog-changes", (caller, ct) => replica.ReadCatalogChangesAsync(libraryId, caller, after, upper, limit ?? CatalogFeedProtocol.MaximumPageSize, ct), token);

    private static Task<IResult> NotificationsAsync(Guid libraryId, int? limit, HttpContext context, IHostAccessTokenVerifier verifier,
        IHostCatalogReplication replica, CancellationToken token) => ReadAsync(libraryId, context, verifier, "catalog-notifications",
        (caller, ct) => replica.ReadCatalogNotificationsAsync(libraryId, caller, limit ?? CatalogFeedProtocol.MaximumPageSize, ct), token);

    private static Task<IResult> AcknowledgeAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        IHostCatalogReplication replica, CancellationToken token) => HostLibraryMutation.RunAsync<CatalogAcknowledgement, CatalogAcknowledgementReceipt>(
        libraryId, context, verifier, replica.AcknowledgeCatalogAsync, "catalog-ack", token);

    /// <summary>Captures the exact host catalog before paging for FR-RECORDS-001#ac-replicate.</summary>
    private static Task<IResult> BeginSnapshotAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        IHostCatalogReplication replica, CancellationToken token) => HostLibraryMutation.RunAsync<BeginCatalogSnapshot, CatalogSnapshotPage>(
        libraryId, context, verifier, replica.BeginCatalogSnapshotAsync, "catalog-snapshot-begin", token);

    /// <summary>Reads the previously captured recipient page for FR-RECORDS-001#ac-replicate.</summary>
    private static Task<IResult> SnapshotPageAsync(Guid libraryId, Guid snapshotId, int offset, int? limit, HttpContext context,
        IHostAccessTokenVerifier verifier, IHostCatalogReplication replica, CancellationToken token) => ReadAsync(libraryId, context, verifier,
        "catalog-snapshot-page", (caller, ct) => replica.ReadCatalogSnapshotAsync(libraryId, caller, snapshotId, offset,
            limit ?? CatalogFeedProtocol.MaximumPageSize, ct), token);

    private static async Task<IResult> ReadAsync<T>(Guid library, HttpContext context, IHostAccessTokenVerifier verifier, string operation,
        Func<AuthenticatedHostInstance, CancellationToken, Task<HostOutcome<T>>> read, CancellationToken token)
    {
        var (caller, failure) = await HostLibraryMutation.AuthorizeAsync(library, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        context.Response.Headers.CacheControl = "no-store";
        return HostLibraryMutation.ToHttpResult(await read(caller!, token).ConfigureAwait(false), operation);
    }
}
