using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Identifies the receiver API and folder selected from trusted host registration.</summary>
/// <remarks>2026-11-24: Scope receiver index queries for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
/// <remarks>2026-11-24: Carry the native sender and approved root for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record SyncthingReceiver(Uri Endpoint, string DeviceId, string FolderId, string? ApprovedRoot = null, string? ApprovedSenderId = null)
{
    /// <summary>Builds the exact native scope from a pinned approval or its older configured sender route.</summary>
    internal static SyncthingReceiver FromRegistration(RegisteredAndroidExport target, ISyncthingExportSenderRegistry? senders) =>
        new(target.ReceiverEndpoint, target.SyncthingDeviceId, target.ShareId, target.ReceiverRoot,
            target.ApprovedSenderId ?? (target.ReceiverRoot is null ? null : senders?.Find(target)?.DeviceId));
}

/// <summary>Reads exact relative paths from the selected receiver's own Syncthing index.</summary>
/// <remarks>2026-11-24: Distinguish receiver-local files from global availability for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class SyncthingReceiverInventory(HttpClient client)
{
    private const string NotIndexed = "No such object in the index";
    private const int ReceiveOnlyChanged = 8;
    private const int GlobalIndexFlag = 16;
    private const int NeededIndexFlag = 32;

    /// <summary>Verifies current acceptance of the paired folder without consulting the file index.</summary>
    /// <remarks>2026-11-24: Read Android share status for FR-FILESYNC-001#ac-layouts.</remarks>
    internal async Task<HostOutcome<AndroidShareCondition>> ReadShareStatusAsync(SyncthingReceiver receiver, string credential, CancellationToken token)
    {
        if (!IsTrustedEndpoint(receiver.Endpoint) || string.IsNullOrWhiteSpace(receiver.DeviceId) ||
            string.IsNullOrWhiteSpace(receiver.FolderId) || string.IsNullOrWhiteSpace(credential))
            return new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-folder-unavailable");
        try
        {
            var root = new Uri(receiver.Endpoint.AbsoluteUri.TrimEnd('/') + '/');
            using var response = await GetAsync(root, "rest/system/status", credential, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-folder-unavailable");
            var identity = await ReadJsonAsync<ReceiverIdentity>(response, token).ConfigureAwait(false);
            return identity?.MyId == receiver.DeviceId
                ? await SyncthingReceiverFolder.ReadAsync(client, root, credential, receiver, token).ConfigureAwait(false)
                : new HostOutcome<AndroidShareCondition>.Rejected("export-receiver-device-mismatch");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<AndroidShareCondition>.Cancelled(); }
        catch (Exception exception) when (SyncthingHostExportScanner.IsUnavailable(exception))
        { return new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-folder-unavailable"); }
    }

    /// <summary>Reports whether each supplied path is indexed locally in a stable receiver snapshot.</summary>
    /// <param name="receiver">The registered peer and its device export share.</param>
    /// <param name="destinations">Validated share-relative audio paths. Identical paths are read once.</param>
    /// <param name="credential">The receiver API key obtained through the host credential boundary.</param>
    /// <param name="token">Cancels the snapshot read.</param>
    public async Task<HostOutcome<ImmutableDictionary<string, bool>>> ReadAsync(SyncthingReceiver receiver,
        IEnumerable<string> destinations, string credential, CancellationToken token)
    {
        if (!IsTrustedEndpoint(receiver.Endpoint) || string.IsNullOrWhiteSpace(receiver.DeviceId) ||
            string.IsNullOrWhiteSpace(receiver.FolderId) || string.IsNullOrWhiteSpace(credential))
            return Unavailable("export-inventory-registration-unavailable");

        var paths = destinations.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Any(path => !PlaylistExportPlanner.TryNormalize(path, out var normalized) || path != normalized))
            return new HostOutcome<ImmutableDictionary<string, bool>>.Rejected("unsafe-device-destination");
        var files = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.Ordinal);
        try
        {
            var root = new Uri(receiver.Endpoint.AbsoluteUri.TrimEnd('/') + '/');
            using var identityResponse = await GetAsync(root, "rest/system/status", credential, token).ConfigureAwait(false);
            var receiverIdentity = await ReadJsonAsync<ReceiverIdentity>(identityResponse, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(receiverIdentity?.MyId))
                return Unavailable();
            if (receiverIdentity.MyId != receiver.DeviceId)
                return Unavailable("export-inventory-device-mismatch");
            var folderReady = await SyncthingReceiverFolder.RequireReadyAsync(client, root, credential, receiver, token).ConfigureAwait(false);
            if (folderReady is not HostOutcome<bool>.Success)
                return folderReady.PropagateFailure<ImmutableDictionary<string, bool>>();

            var folder = Uri.EscapeDataString(receiver.FolderId);
            var statusRoute = $"rest/db/status?folder={folder}";
            using var beforeResponse = await GetAsync(root, statusRoute, credential, token).ConfigureAwait(false);
            var before = await ReadJsonAsync<FolderStatus>(beforeResponse, token).ConfigureAwait(false);
            if (before is not { State: "idle", Sequence: not null, StateChanged: not null, IgnorePatterns: not null })
                return Unavailable();

            foreach (var path in paths)
            {
                var route = $"rest/db/file?folder={folder}&file={Uri.EscapeDataString(path)}";
                using var response = await GetAsync(root, route, credential, token).ConfigureAwait(false);
                var status = await ReadFileAsync(response, path, token).ConfigureAwait(false);
                switch (status)
                {
                    case IndexedFile.Present:
                        files.Add(path, true);
                        break;
                    case IndexedFile.Missing when before.IgnorePatterns == false:
                        files.Add(path, false);
                        break;
                    case IndexedFile.Unsafe:
                        return new HostOutcome<ImmutableDictionary<string, bool>>.Rejected("unsafe-device-destination");
                    case IndexedFile.Missing or IndexedFile.Hidden:
                        return Unavailable("export-inventory-destination-hidden");
                    default:
                        return Unavailable();
                }
            }

            using var afterResponse = await GetAsync(root, statusRoute, credential, token).ConfigureAwait(false);
            var after = await ReadJsonAsync<FolderStatus>(afterResponse, token).ConfigureAwait(false);
            if (before != after)
                return Unavailable("export-inventory-index-changed");
            folderReady = await SyncthingReceiverFolder.RequireReadyAsync(client, root, credential, receiver, token).ConfigureAwait(false);
            if (folderReady is not HostOutcome<bool>.Success)
                return folderReady.PropagateFailure<ImmutableDictionary<string, bool>>();
            return new HostOutcome<ImmutableDictionary<string, bool>>.Success(files.ToImmutable());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<ImmutableDictionary<string, bool>>.Cancelled();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or JsonException or FormatException)
        {
            return Unavailable();
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri root, string relative, string credential, CancellationToken token)
    {
        var destination = new Uri(root, relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        request.Headers.Authorization = new("Bearer", credential);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri == destination)
            return response;
        response.Dispose();
        throw new HttpRequestException("The inventory response did not come from the registered receiver endpoint.");
    }

    private static Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken token) where T : class =>
        response.IsSuccessStatusCode ? response.Content.ReadFromJsonAsync<T>(token) : Task.FromResult<T?>(null);

    private static async Task<IndexedFile> ReadFileAsync(HttpResponseMessage response, string path, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType == "text/plain" &&
            (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).TrimEnd('\r', '\n') == NotIndexed)
            return IndexedFile.Missing;

        var snapshot = await ReadJsonAsync<FileSnapshot>(response, token).ConfigureAwait(false);
        if (snapshot?.Local is not { Name: not null, Deleted: not null, Invalid: not null, Ignored: not null, MustRescan: not null } local)
            return IndexedFile.Unknown;
        if (local.Ignored == true)
            return IndexedFile.Hidden;
        if (local.MustRescan == true)
            return IndexedFile.Unknown;
        if (local.Name.Length == 0 && local is { Deleted: false, Invalid: false } && snapshot.Global?.Name == path)
            return IndexedFile.Missing;
        if (local.Name != path)
            return IndexedFile.Unknown;

        // A receive-only local change is intentionally invalid to peers but is still present on this receiver.
        var flags = local.LocalFlags.GetValueOrDefault() & ~(GlobalIndexFlag | NeededIndexFlag);
        if (local.Invalid == true && flags != ReceiveOnlyChanged)
            return IndexedFile.Unknown;
        if (local.Deleted == true)
            return IndexedFile.Missing;
        return local.Type switch
        {
            { ValueKind: JsonValueKind.String } type when type.GetString() == "FILE_INFO_TYPE_FILE" => IndexedFile.Present,
            { ValueKind: JsonValueKind.Number } type when type.TryGetInt32(out var number) && number == 0 => IndexedFile.Present,
            { ValueKind: JsonValueKind.String or JsonValueKind.Number } => IndexedFile.Unsafe,
            _ => IndexedFile.Unknown
        };
    }

    internal static bool IsTrustedEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri &&
        endpoint.UserInfo.Length == 0 && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 &&
        (endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp &&
            IPAddress.TryParse(endpoint.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip));

    private static HostOutcome<ImmutableDictionary<string, bool>> Unavailable(string code = "export-inventory-unavailable") =>
        new HostOutcome<ImmutableDictionary<string, bool>>.Unavailable(code);

    private enum IndexedFile { Unknown, Missing, Present, Hidden, Unsafe }
    private sealed record ReceiverIdentity(string? MyId);
    private sealed record FolderStatus(string? State, long? Sequence, DateTimeOffset? StateChanged, bool? IgnorePatterns);
    private sealed record FileSnapshot(IndexEntry? Local, IndexEntry? Global);
    private sealed record IndexEntry(string? Name, bool? Deleted, bool? Invalid, bool? Ignored, bool? MustRescan, int? LocalFlags, JsonElement Type);
}
