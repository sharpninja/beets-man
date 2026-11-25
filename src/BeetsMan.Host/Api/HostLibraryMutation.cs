using System.Net.Http.Headers;
using System.Text.Json;
using BeetsMan.Host.Application;
using Microsoft.AspNetCore.Http;

namespace BeetsMan.Host.Api;

/// <summary>Enforces the registered library HTTPS boundary for host mutations.</summary>
/// <remarks>2026-11-24: Share registered access checks between FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-playlist-drop.</remarks>
internal static class HostLibraryMutation
{
    /// <summary>Authenticates and invokes a typed host command, preserving its scoped problem codes.</summary>
    internal static async Task<IResult> RunAsync<TRequest, TResponse>(Guid libraryId, HttpContext context,
        IHostAccessTokenVerifier verifier, Func<Guid, AuthenticatedHostInstance, TRequest, CancellationToken, Task<HostOutcome<TResponse>>> execute,
        string operation, CancellationToken token) where TRequest : class
    {
        var (caller, failure) = await AuthorizeAsync(libraryId, context, verifier, operation, token).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        if (!context.Request.HasJsonContentType())
            return Problem($"invalid-{operation}-content-type", StatusCodes.Status415UnsupportedMediaType);

        TRequest? command;
        try { command = await context.Request.ReadFromJsonAsync<TRequest>(token).ConfigureAwait(false); }
        catch (JsonException) { return Problem($"invalid-{operation}-json", StatusCodes.Status400BadRequest); }
        if (command is null)
            return Problem($"invalid-{operation}-json", StatusCodes.Status400BadRequest);

        var result = await execute(libraryId, caller!, command, token).ConfigureAwait(false);
        return ToHttpResult(result, operation);
    }

    /// <summary>Checks the authenticated HTTPS library scope used by both queries and commands.</summary>
    /// <remarks>2026-11-24: Preserve the shared registration boundary for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
    internal static async Task<(AuthenticatedHostInstance? Caller, IResult? Failure)> AuthorizeAsync(Guid libraryId, HttpContext context,
        IHostAccessTokenVerifier verifier, string operation, CancellationToken token)
    {
        if (!context.Request.IsHttps)
            return (null, Problem("https-required", StatusCodes.Status426UpgradeRequired));
        var caller = await VerifyAsync(context, verifier, token).ConfigureAwait(false);
        if (caller is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return (null, Problem($"{operation}-credentials-required", StatusCodes.Status401Unauthorized));
        }
        return caller.LibraryId == libraryId ? (caller, null) : (null, Problem($"{operation}-access-denied", StatusCodes.Status403Forbidden));
    }

    /// <summary>Maps typed library outcomes to a versioned HTTPS result.</summary>
    internal static IResult ToHttpResult<TResponse>(HostOutcome<TResponse> result, string operation) => result switch
        {
            HostOutcome<TResponse>.Success success => Results.Ok(success.Value),
            HostOutcome<TResponse>.Forbidden => Problem($"{operation}-access-denied", StatusCodes.Status403Forbidden),
            HostOutcome<TResponse>.Rejected rejection => Problem(rejection.Code,
                rejection.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, rejection.ActualRevision),
            HostOutcome<TResponse>.Cancelled => Problem($"{operation}-request-cancelled", StatusCodes.Status408RequestTimeout),
            HostOutcome<TResponse>.Unavailable unavailable => Problem(unavailable.Code, StatusCodes.Status503ServiceUnavailable),
            _ => throw new InvalidOperationException("Unexpected host library outcome.")
        };

    private static async ValueTask<AuthenticatedHostInstance?> VerifyAsync(HttpContext context, IHostAccessTokenVerifier verifier, CancellationToken token)
    {
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
            return null;
        return await verifier.VerifyAsync(header.Parameter, token).ConfigureAwait(false);
    }

    private static IResult Problem(string code, int status, long? actualRevision = null) =>
        Results.Json(new { Code = code, ActualRevision = actualRevision }, statusCode: status);
}
