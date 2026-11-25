using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Retrieves the host access token through the registered desktop credential boundary.</summary>
/// <remarks>2026-11-24: Keep credentials outside playlist storage for FR-RECORDS-001#ac-register.</remarks>
public interface IRegisteredHostTokenProvider
{
    /// <summary>Returns a token for the given library and instance, or no token if unavailable.</summary>
    ValueTask<string?> GetTokenAsync(Guid libraryId, Guid instanceId, CancellationToken cancellationToken);
}

/// <summary>Sends replayable playlist saves to the registered HTTPS host.</summary>
/// <remarks>2026-11-24: Bind the desktop playlist publication port for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed class HttpPlaylistPublicationHost : IPlaylistPublicationHost
{
    private readonly HttpClient client;
    private readonly Guid libraryId;
    private readonly IRegisteredHostTokenProvider credentials;
    private readonly Uri endpoint;

    /// <summary>Creates an adapter for the configured host and its library.</summary>
    public HttpPlaylistPublicationHost(HttpClient client, Uri host, Guid libraryId, IRegisteredHostTokenProvider credentials)
    {
        this.client = client;
        this.libraryId = libraryId;
        this.credentials = credentials;
        endpoint = RegisteredHostEndpoint.Resolve(host, libraryId, PlaylistPublicationRoute.Path);
    }

    /// <inheritdoc />
    public async Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken cancellationToken)
    {
        var key = operation.Key;
        if (request.OriginInstanceId is { } origin && key.OriginInstanceId != origin)
            return Invalid("playlist-origin-mismatch");
        if (operation.QueueSource is { ExpectedRevisionPinned: false })
            return Invalid("queue-save-revision-not-pinned");
        try
        {
            var token = await credentials.GetTokenAsync(libraryId, key.OriginInstanceId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-credentials-unavailable", true);
            var source = operation.QueueSource is { } queue
                ? new PlaylistSaveSource(PlaylistSaveSourceKind.Queue, queue.SessionId)
                : new PlaylistSaveSource(PlaylistSaveSourceKind.Draft, operation.DraftId);
            var body = new PlaylistSaveRequest(key.OriginInstanceId, key.OperationId, source, operation.BasePlaylist?.PlaylistId,
                operation.BasePlaylist?.Revision, operation.Name, [.. operation.Entries.Select(entry => new PlaylistEntryData(entry.EntryId, entry.TrackId))]);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) };
            message.Headers.Authorization = new("Bearer", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != endpoint)
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-endpoint-changed", false);
            if (!response.IsSuccessStatusCode)
                return await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<PlaylistSaveResponse>(cancellationToken).ConfigureAwait(false);
            if (result is null || result.OperationId != key.OperationId || result.Source != source || result.PlaylistId == Guid.Empty ||
                result.FeedRevision <= 0 || result.PlaylistRevision <= 0 || operation.BasePlaylist is { } backing &&
                (result.PlaylistId != backing.PlaylistId || result.PlaylistRevision <= backing.Revision))
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-host-receipt-mismatch", true);
            return new Outcome<PlaylistSavedReceipt>.Success(new(operation.DraftId, result.PlaylistId, result.PlaylistRevision,
                QueueSessionId: operation.QueueSource?.SessionId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new Outcome<PlaylistSavedReceipt>.Cancelled(); }
        catch (OperationCanceledException) { return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-host-timeout", true); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-host-unreachable", true);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new Outcome<PlaylistSavedReceipt>.DependencyFailure("invalid-playlist-host-response", true);
        }
    }

    private static async Task<Outcome<PlaylistSavedReceipt>> ReadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Invalid("playlist-access-denied");
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<PlaylistSaveProblem>(cancellationToken).ConfigureAwait(false);
            if (problem?.Code is not { Length: > 0 and <= 100 } code)
                return new Outcome<PlaylistSavedReceipt>.DependencyFailure("invalid-playlist-host-response", true);
            return response.StatusCode == HttpStatusCode.Conflict
                ? new Outcome<PlaylistSavedReceipt>.Conflict(code, problem.ActualRevision) : Invalid(code);
        }
        return new Outcome<PlaylistSavedReceipt>.DependencyFailure("playlist-host-request-failed",
            (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
    }

    private static Outcome<PlaylistSavedReceipt> Invalid(string code) => new Outcome<PlaylistSavedReceipt>.Invalid([new(code)]);
}
