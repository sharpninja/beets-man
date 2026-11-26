using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Submits an authenticated listening or default change and verifies the original host receipt.</summary>
/// <remarks>2026-11-25: Reuse exact stable operations for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
/// <remarks>2026-11-25: Validate Appearance text before publishing for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed class HttpLibraryRecordHost(HttpClient client, Uri host, Guid library, Guid origin, IRegisteredHostTokenProvider credentials) : ILibraryRecordHost
{
    private readonly Uri endpoint = RegisteredHostEndpoint.Resolve(host, library, LibraryRecordRoutes.PublishPath);

    /// <inheritdoc />
    public Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token)
    {
        var recordId = change switch
        {
            LibraryRecordIntent.Favorite favorite when favorite.TrackId != Guid.Empty => LibraryRecordIdentity.Favorite(library, favorite.TrackId),
            LibraryRecordIntent.Play play => play.PlayId,
            LibraryRecordIntent.SharedDefault setting when SharedLibraryDefaults.IsValid(setting.Key, setting.DurationTicks, setting.Flag, setting.Text) =>
                LibraryRecordIdentity.SharedDefault(library, setting.Key),
            _ => Guid.Empty
        };
        if (operation == Guid.Empty || origin == Guid.Empty || recordId == Guid.Empty)
            return Task.FromResult<Outcome<LibraryRecordReceipt>>(new Outcome<LibraryRecordReceipt>.Invalid([new("invalid-library-record")]));
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(new PublishLibraryRecordRequest(origin, operation, change)) };
        return RegisteredHostEndpoint.SendAsync<LibraryRecordReceipt, LibraryRecordReceipt>(client, credentials, library, request, origin,
            "library-record", "library-record-receipt-mismatch", receipt => receipt.LibraryId == library && receipt.OriginInstanceId == origin &&
                receipt.OperationId == operation && receipt.RecordId == recordId && receipt.RecordRevision >= 0 &&
                receipt.FeedRevision >= receipt.RecordRevision ? receipt : null, token);
    }
}
