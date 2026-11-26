using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Pulls catalog revisions and acknowledges them on the registered HTTPS host.</summary>
/// <remarks>2026-11-24: Preserve the authenticated recipient, requested window and committed acknowledgement for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
/// <remarks>2026-11-24: Recover the same reviewed cursor through pinned snapshot pages for FR-RECORDS-001#ac-offline.</remarks>
public sealed class HttpCatalogReplicaHost(HttpClient client, Uri host, Guid library, Guid recipient, IRegisteredHostTokenProvider credentials,
    int pageSize = CatalogFeedProtocol.MaximumPageSize) : ICatalogReplicaHost
{
    private readonly Uri acknowledgement = RegisteredHostEndpoint.Resolve(host, library, CatalogReplicationRoutes.AcknowledgePath);
    private readonly Uri snapshots = RegisteredHostEndpoint.Resolve(host, library, CatalogSnapshotRoutes.BeginPath);

    /// <inheritdoc />
    public Task<Outcome<CatalogFeedPage>> ReadChangesAsync(long after, long? upper, CancellationToken token)
    {
        var endpoint = RegisteredHostEndpoint.Resolve(host, library, id => CatalogReplicationRoutes.ChangesPath(id, after, upper, pageSize));
        return RegisteredHostEndpoint.SendAsync<CatalogFeedPage, CatalogFeedPage>(client, credentials, library, new(HttpMethod.Get, endpoint), recipient,
            "catalog-changes", "catalog-page-mismatch", page => page.LibraryId == library && page.RecipientInstanceId == recipient &&
                page.AfterRevision == after && (!upper.HasValue || page.UpperRevision == upper) && CatalogFeedProtocol.IsValid(page) &&
                page.Revisions.Length <= pageSize ? page : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<CatalogAcknowledgementReceipt>> AcknowledgeAsync(long through, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, acknowledgement) { Content = JsonContent.Create(new CatalogAcknowledgement(recipient, through)) };
        return RegisteredHostEndpoint.SendAsync<CatalogAcknowledgementReceipt, CatalogAcknowledgementReceipt>(client, credentials, library, request, recipient,
            "catalog-ack", "catalog-ack-mismatch", reply => reply.LibraryId == library && reply.RecipientInstanceId == recipient &&
                reply.ThroughRevision == through ? reply : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<CatalogSnapshotPage>> BeginSnapshotAsync(Guid snapshotId, long expectedCursor, CancellationToken token) =>
        ReadSnapshotPageAsync(new HttpRequestMessage(HttpMethod.Post, snapshots)
            { Content = JsonContent.Create(new BeginCatalogSnapshot(recipient, snapshotId, expectedCursor, pageSize)) }, snapshotId, 0,
            "catalog-snapshot-begin", token);

    /// <inheritdoc />
    public Task<Outcome<CatalogSnapshotPage>> ReadSnapshotAsync(Guid snapshotId, int offset, CancellationToken token)
    {
        var endpoint = RegisteredHostEndpoint.Resolve(host, library, id => CatalogSnapshotRoutes.PagePath(id, snapshotId, offset, pageSize));
        return ReadSnapshotPageAsync(new(HttpMethod.Get, endpoint), snapshotId, offset, "catalog-snapshot-page", token);
    }

    private Task<Outcome<CatalogSnapshotPage>> ReadSnapshotPageAsync(HttpRequestMessage request, Guid snapshotId, int offset, string operation,
        CancellationToken token) => RegisteredHostEndpoint.SendAsync<CatalogSnapshotPage, CatalogSnapshotPage>(client, credentials, library, request, recipient,
        operation, "catalog-snapshot-page-mismatch", page => page.LibraryId == library && page.RecipientInstanceId == recipient &&
            page.SnapshotId == snapshotId && page.Offset == offset && CatalogSnapshotProtocol.IsValid(page) && page.Records.Length <= pageSize ? page : null, token);
}
