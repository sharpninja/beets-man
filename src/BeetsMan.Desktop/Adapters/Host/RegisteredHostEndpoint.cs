using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Resolves command paths relative to the registered library host.</summary>
/// <remarks>2026-11-24: Share endpoint validation for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-playlist-drop.</remarks>
/// <remarks>2026-11-24: Share authenticated command transport with the guided import host for FR-BEETS-001#ac-import.</remarks>
internal static class RegisteredHostEndpoint
{
    /// <summary>Builds an HTTPS endpoint under the configured host base path and library ID.</summary>
    internal static Uri Resolve(Uri host, Guid libraryId, Func<Guid, string> commandPath)
    {
        if (!host.IsAbsoluteUri || host.Scheme != Uri.UriSchemeHttps || host.UserInfo.Length != 0 ||
            host.Query.Length != 0 || host.Fragment.Length != 0 || libraryId == Guid.Empty)
            throw new ArgumentException("A registered HTTPS host and library are required.", nameof(host));
        return new(new Uri(host.AbsoluteUri.TrimEnd('/') + '/'), commandPath(libraryId));
    }

    /// <summary>Sends a scoped command or read to the configured host and verifies its typed response.</summary>
    internal static async Task<Outcome<TResult>> SendAsync<TWire, TResult>(HttpClient client, IRegisteredHostTokenProvider credentials,
        Guid libraryId, HttpRequestMessage request, Guid origin, string operation, string mismatchCode, Func<TWire, TResult?> project,
        CancellationToken token) where TWire : class where TResult : class
    {
        using var message = request;
        var requested = message.RequestUri;
        try
        {
            var credential = await credentials.GetTokenAsync(libraryId, origin, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
                return new Outcome<TResult>.DependencyFailure($"{operation}-credentials-unavailable", true);
            message.Headers.Authorization = new("Bearer", credential);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != requested)
                return new Outcome<TResult>.DependencyFailure($"{operation}-endpoint-changed", false);
            if (!response.IsSuccessStatusCode)
                return await ReadFailureAsync<TResult>(response, operation, token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<TWire>(token).ConfigureAwait(false);
            return result is not null && project(result) is { } value ? new Outcome<TResult>.Success(value) :
                new Outcome<TResult>.DependencyFailure(mismatchCode, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<TResult>.Cancelled(); }
        catch (OperationCanceledException) { return new Outcome<TResult>.DependencyFailure($"{operation}-host-timeout", true); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new Outcome<TResult>.DependencyFailure($"{operation}-host-unreachable", true);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new Outcome<TResult>.DependencyFailure($"invalid-{operation}-response", true);
        }
    }

    private static async Task<Outcome<TResult>> ReadFailureAsync<TResult>(HttpResponseMessage response, string operation, CancellationToken token)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new Outcome<TResult>.Invalid([new($"{operation}-access-denied")]);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<HostProblem>(token).ConfigureAwait(false);
            if (problem?.Code is not { Length: > 0 and <= 100 } code)
                return new Outcome<TResult>.DependencyFailure($"invalid-{operation}-response", true);
            return response.StatusCode == HttpStatusCode.Conflict ? new Outcome<TResult>.Conflict(code, problem.ActualRevision) :
                new Outcome<TResult>.Invalid([new(code)]);
        }
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Content.Headers.ContentType?.MediaType == "application/json" &&
            await response.Content.ReadFromJsonAsync<HostProblem>(token).ConfigureAwait(false) is { Code.Length: > 0 and <= 100 } unavailable)
            return new Outcome<TResult>.DependencyFailure(unavailable.Code, true);
        return new Outcome<TResult>.DependencyFailure($"{operation}-host-request-failed",
            (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
    }

    private sealed record HostProblem(string Code, long? ActualRevision);
}
