using System.Net;
using System.Net.Http.Json;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Controls one receive-only PC mirror after checking the sender and receiver registrations.</summary>
/// <remarks>2026-11-24: Implement the native STB10 scope, progress, pause, resume and rescan for FR-FILESYNC-001#ac-mirror.</remarks>
public sealed class SyncthingPcMirrorControl(HttpClient client, IRegisteredSyncthingTokenProvider receivers, IHostSenderTokenProvider senders)
    : IPcMirrorControl
{
    /// <inheritdoc />
    public async Task<HostOutcome<PcMirrorNativeState>> ApplyAsync(RegisteredPcMirror target, PcMirrorAction action,
        PcMirrorNativeState? previous, CancellationToken token)
    {
        if (target.LibraryId == Guid.Empty || target.PeerId == Guid.Empty || string.IsNullOrWhiteSpace(target.FolderId) || target.FolderId is "." or ".." ||
            string.IsNullOrWhiteSpace(target.SenderDeviceId) || string.IsNullOrWhiteSpace(target.ReceiverDeviceId) ||
            target.SenderDeviceId == target.ReceiverDeviceId || !Enum.IsDefined(action) ||
            !SyncthingReceiverInventory.IsTrustedEndpoint(target.SenderEndpoint) || !SyncthingReceiverInventory.IsTrustedEndpoint(target.ReceiverEndpoint) ||
            !Path.IsPathFullyQualified(target.MusicRoot) || !PcMirrorNativePath.IsDedicatedRoot(target.ReceiverRoot))
            return Rejected("invalid-mirror-scope");

        try
        {
            var senderCredential = await senders.GetTokenAsync(target.LibraryId, target.SenderDeviceId, token).ConfigureAwait(false);
            var receiverCredential = await receivers.GetTokenAsync(target.LibraryId, target.PeerId, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(senderCredential) || string.IsNullOrWhiteSpace(receiverCredential))
                return Unavailable("mirror-credentials-unavailable");
            var source = new Endpoint(Root(target.SenderEndpoint), senderCredential);
            var receiver = new Endpoint(Root(target.ReceiverEndpoint), receiverCredential);
            var folderId = Uri.EscapeDataString(target.FolderId);
            var folderRoute = $"rest/config/folders/{folderId}";

            var sourceIdentity = await ReadAsync<Identity>(source, "rest/system/status", token).ConfigureAwait(false);
            if (sourceIdentity is null)
                return Unavailable("mirror-sender-unavailable");
            if (sourceIdentity.DeviceId != target.SenderDeviceId)
                return Rejected("mirror-sender-identity-mismatch");
            var senderFolder = await ReadAsync<Folder>(source, folderRoute, token).ConfigureAwait(false);
            if (!MatchesSender(senderFolder, target))
                return Rejected("mirror-sender-folder-mismatch");

            var receiverIdentity = await ReadAsync<Identity>(receiver, "rest/system/status", token).ConfigureAwait(false);
            if (receiverIdentity is null)
                return Unavailable("mirror-receiver-unavailable");
            if (receiverIdentity.DeviceId != target.ReceiverDeviceId)
                return Rejected("mirror-receiver-identity-mismatch");
            var connections = await ReadAsync<PeerConnections>(receiver, "rest/system/connections", token).ConfigureAwait(false);
            if (connections?.Connections is null)
                return Unavailable("mirror-connection-status-unavailable");
            var connected = connections.Connections.GetValueOrDefault(target.SenderDeviceId)?.Connected == true;

            using var configured = await SendAsync(receiver, HttpMethod.Get, folderRoute, null, token).ConfigureAwait(false);
            if (configured.StatusCode == HttpStatusCode.NotFound)
                return action == PcMirrorAction.Observe
                    ? Success(new(connected, false, PcMirrorPhase.AwaitingAcceptance, null, null))
                    : Rejected("mirror-share-not-accepted");
            if (configured.StatusCode != HttpStatusCode.OK)
                return Unavailable("mirror-receiver-folder-unavailable");
            var folder = await configured.Content.ReadFromJsonAsync<Folder>(token).ConfigureAwait(false);
            if (!MatchesReceiver(folder, target))
                return Rejected("mirror-receiver-folder-mismatch");
            var isPaused = folder!.Paused!.Value;
            if (action is PcMirrorAction.Pause or PcMirrorAction.Resume)
            {
                var requestedPause = action == PcMirrorAction.Pause;
                if (requestedPause != isPaused)
                {
                    using var patch = await SendAsync(receiver, HttpMethod.Patch, folderRoute, JsonContent.Create(new { paused = requestedPause }), token)
                        .ConfigureAwait(false);
                    if (!patch.IsSuccessStatusCode)
                        return Unavailable("mirror-pause-unavailable");
                    var applied = await ReadAsync<Folder>(receiver, folderRoute, token).ConfigureAwait(false);
                    if (!MatchesReceiver(applied, target) || applied!.Paused != requestedPause)
                        return Unavailable("mirror-pause-unverified");
                    isPaused = requestedPause;
                }
            }
            else if (action == PcMirrorAction.Rescan)
            {
                if (isPaused)
                    return Rejected("mirror-share-paused");
                using var scan = await SendAsync(receiver, HttpMethod.Post, $"rest/db/scan?folder={folderId}", null, token).ConfigureAwait(false);
                if (scan.StatusCode != HttpStatusCode.OK)
                    return Unavailable("mirror-rescan-unavailable");
            }

            if (isPaused)
                return Success(new(connected, true, PcMirrorPhase.Paused, previous?.FilesReceived, previous?.TotalFiles));
            var status = await ReadAsync<FolderStatus>(receiver, $"rest/db/status?folder={folderId}", token).ConfigureAwait(false);
            if (status is not { InSyncFiles: >= 0, GlobalFiles: >= 0, NeedTotalItems: >= 0, PullErrors: >= 0, State.Length: > 0 } ||
                status.InSyncFiles > status.GlobalFiles)
                return Unavailable("mirror-progress-unavailable");
            var failed = status.PullErrors > 0 || status.State == "error";
            var phase = failed ? PcMirrorPhase.Failed : !connected ? PcMirrorPhase.Disconnected :
                status.NeedTotalItems == 0 && status.InSyncFiles == status.GlobalFiles && status.State == "idle"
                    ? PcMirrorPhase.Complete : PcMirrorPhase.Transferring;
            return Success(new(connected, false, phase, status.InSyncFiles, status.GlobalFiles, failed ? "mirror-transfer-failed" : null));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<PcMirrorNativeState>.Cancelled(); }
        catch (Exception error) when (SyncthingHostExportScanner.IsUnavailable(error))
        {
            return Unavailable("mirror-control-unavailable");
        }
    }

    private async Task<T?> ReadAsync<T>(Endpoint endpoint, string path, CancellationToken token) where T : class
    {
        using var reply = await SendAsync(endpoint, HttpMethod.Get, path, null, token).ConfigureAwait(false);
        return reply.StatusCode == HttpStatusCode.OK ? await reply.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false) : null;
    }

    private Task<HttpResponseMessage> SendAsync(Endpoint endpoint, HttpMethod method, string path, HttpContent? content, CancellationToken token) =>
        SyncthingHostExportScanner.SendAsync(client, endpoint.Root, method, path, endpoint.Credential, token, content);

    private static bool MatchesSender(Folder? folder, RegisteredPcMirror target) =>
        folder is { Type: "sendonly", Path: { Length: > 0 } path, Devices: { Length: >= 2 } devices } && folder.Id == target.FolderId &&
        Path.IsPathFullyQualified(path) && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.MusicRoot)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
        Participants(devices).IsSupersetOf([target.SenderDeviceId, target.ReceiverDeviceId]);

    private static bool MatchesReceiver(Folder? folder, RegisteredPcMirror target) =>
        folder is { Type: "receiveonly", Paused: not null, Path: { Length: > 0 } path, Devices: { Length: 2 } devices } &&
        folder.Id == target.FolderId && PcMirrorNativePath.Matches(path, target.ReceiverRoot) &&
        Participants(devices).SetEquals([target.SenderDeviceId, target.ReceiverDeviceId]);

    private static HashSet<string?> Participants(IEnumerable<Device?> devices) => devices.Select(device => device?.DeviceId).ToHashSet(StringComparer.Ordinal);

    private static Uri Root(Uri endpoint) => new(endpoint.AbsoluteUri.TrimEnd('/') + '/');
    private static HostOutcome<PcMirrorNativeState> Success(PcMirrorNativeState state) => new HostOutcome<PcMirrorNativeState>.Success(state);
    private static HostOutcome<PcMirrorNativeState> Rejected(string code) => new HostOutcome<PcMirrorNativeState>.Rejected(code);
    private static HostOutcome<PcMirrorNativeState> Unavailable(string code) => new HostOutcome<PcMirrorNativeState>.Unavailable(code);

    private sealed record Endpoint(Uri Root, string Credential);
    private sealed record Identity([property: System.Text.Json.Serialization.JsonPropertyName("myID")] string? DeviceId);
    private sealed record Device(string? DeviceId);
    private sealed record Folder(string? Id, string? Type, string? Path, bool? Paused, Device?[]? Devices);
    private sealed record PeerConnections(Dictionary<string, Peer?>? Connections);
    private sealed record Peer(bool? Connected);
    private sealed record FolderStatus(long? InSyncFiles, long? GlobalFiles, long? NeedTotalItems, long? PullErrors, string? State);
}
