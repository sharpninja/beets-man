using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Identifies the configured Syncthing sender and exact local export root.</summary>
/// <remarks>2026-11-24: Bind the sender to the per-device send-only export for TR-FILESYNC-ARCH-001 and FR-FILESYNC-001#ac-safety.</remarks>
public sealed record SyncthingExportSender(Uri Endpoint, string DeviceId, string ExportRoot);

/// <summary>Resolves a host-controlled sender configuration separately from the sender credential.</summary>
public interface ISyncthingExportSenderRegistry
{
    /// <summary>Gets the sender configured for the registered target.</summary>
    SyncthingExportSender? Find(RegisteredAndroidExport target);

    /// <summary>Reads the API credential from the host credential service.</summary>
    ValueTask<string?> GetTokenAsync(RegisteredAndroidExport target, CancellationToken token);
}

/// <summary>Checks the sender and folder scope and asks Syncthing to index committed export files.</summary>
/// <remarks>2026-11-24: Scope and verify the Syncthing REST scan request for FR-FILESYNC-001#ac-safety and TEST-FILESYNC-002.</remarks>
public sealed class SyncthingHostExportScanner(HttpClient client, ISyncthingExportSenderRegistry registry) : IDeviceExportSenderScanner
{
    /// <inheritdoc />
    public async Task<HostOutcome<bool>> ScanAsync(RegisteredAndroidExport target, CancellationToken token)
    {
        var opened = await OpenSenderAsync(target, token).ConfigureAwait(false);
        if (opened is not HostOutcome<SenderConnection>.Success { Value: var sender })
            return opened.PropagateFailure<bool>();
        try
        {
            using var scan = await SendAsync(client, sender.Endpoint, HttpMethod.Post, $"rest/db/scan?folder={Uri.EscapeDataString(target.ShareId)}",
                sender.Credential, token).ConfigureAwait(false);
            return scan.StatusCode == HttpStatusCode.OK ? new HostOutcome<bool>.Success(true) : new HostOutcome<bool>.Unavailable("export-scan-unavailable");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<bool>.Cancelled(); }
        catch (Exception exception) when (IsUnavailable(exception)) { return new HostOutcome<bool>.Unavailable("export-scan-unavailable"); }
    }

    /// <summary>Verifies the configured sender and folder before reading or scanning its index.</summary>
    internal async Task<HostOutcome<SenderConnection>> OpenSenderAsync(RegisteredAndroidExport target, CancellationToken token)
    {
        var sender = registry.Find(target);
        if (sender is not { Endpoint: { } endpoint, ExportRoot.Length: > 0 } || !SyncthingReceiverInventory.IsTrustedEndpoint(endpoint) ||
            string.IsNullOrWhiteSpace(sender.DeviceId) || sender.DeviceId == target.SyncthingDeviceId || string.IsNullOrWhiteSpace(target.ShareId) ||
            !Path.IsPathFullyQualified(sender.ExportRoot))
            return new HostOutcome<SenderConnection>.Rejected("export-scan-sender-unavailable");
        try
        {
            var credential = await registry.GetTokenAsync(target, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
                return new HostOutcome<SenderConnection>.Unavailable("export-scan-credentials-unavailable");

            var root = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/');
            using var identityResponse = await SendAsync(client, root, HttpMethod.Get, "rest/system/status", credential, token).ConfigureAwait(false);
            if (identityResponse.StatusCode != HttpStatusCode.OK)
                return new HostOutcome<SenderConnection>.Unavailable("export-scan-sender-unavailable");
            var identity = await identityResponse.Content.ReadFromJsonAsync<SenderIdentity>(token).ConfigureAwait(false);
            if (identity?.MyId != sender.DeviceId)
                return new HostOutcome<SenderConnection>.Rejected("export-scan-device-mismatch");

            var folderId = Uri.EscapeDataString(target.ShareId);
            using var configResponse = await SendAsync(client, root, HttpMethod.Get, $"rest/config/folders/{folderId}", credential, token).ConfigureAwait(false);
            if (configResponse.StatusCode != HttpStatusCode.OK)
                return new HostOutcome<SenderConnection>.Unavailable("export-scan-folder-unavailable");
            var folder = await configResponse.Content.ReadFromJsonAsync<SenderFolder>(token).ConfigureAwait(false);
            if (!MatchesFolder(folder, sender, target))
                return new HostOutcome<SenderConnection>.Rejected("export-scan-folder-scope-mismatch");
            if (folder!.Paused)
                return new HostOutcome<SenderConnection>.Unavailable("export-scan-folder-paused");
            return new HostOutcome<SenderConnection>.Success(new(root, sender.ExportRoot, credential));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<SenderConnection>.Cancelled(); }
        catch (Exception exception) when (IsUnavailable(exception)) { return new HostOutcome<SenderConnection>.Unavailable("export-scan-unavailable"); }
    }

    private static bool MatchesFolder(SenderFolder? folder, SyncthingExportSender sender, RegisteredAndroidExport target)
    {
        if (folder is not { Type: "sendonly", Path: { Length: > 0 } path, Devices: { Length: 2 } devices } || folder.Id != target.ShareId ||
            !Path.IsPathFullyQualified(path))
            return false;
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sender.ExportRoot));
        var observed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return expected != Path.GetPathRoot(expected) && string.Equals(expected, observed, pathComparison) &&
            new HashSet<string?>(devices.Select(device => device?.DeviceId), StringComparer.Ordinal).SetEquals([sender.DeviceId, target.SyncthingDeviceId]);
    }

    internal static bool IsUnavailable(Exception exception) => exception is HttpRequestException or IOException or OperationCanceledException or JsonException
        or FormatException or ArgumentException;

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri root, HttpMethod method, string path, string credential,
        CancellationToken token, HttpContent? content = null)
    {
        var destination = new Uri(root, path);
        using var request = new HttpRequestMessage(method, destination) { Content = content };
        request.Headers.Authorization = new("Bearer", credential);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri == destination)
            return response;
        response.Dispose();
        throw new HttpRequestException("The sender response changed endpoints.");
    }

    private sealed record SenderIdentity(string? MyId);
    private sealed record SenderFolder(string? Id, string? Type, string? Path, bool Paused, SenderDevice?[]? Devices);
    private sealed record SenderDevice(string? DeviceId);

    internal sealed class SenderConnection(Uri endpoint, string exportRoot, string credential)
    {
        public Uri Endpoint { get; } = endpoint;
        public string ExportRoot { get; } = exportRoot;
        public string Credential { get; } = credential;
    }
}
