using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Offers an authorized media folder on the sender while leaving device acceptance to the receiver.</summary>
/// <remarks>2026-11-24: Scope native pairing for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
public sealed class SyncthingMediaShareOffer(HttpClient client, IRegisteredSyncthingTokenProvider receivers, IHostSenderTokenProvider senders)
    : IMediaShareOffer
{
    private const string Folders = "rest/config/folders";
    private const string Devices = "rest/config/devices";

    /// <inheritdoc />
    public async Task<HostOutcome<MediaShareOffer>> OfferAsync(RegisteredMediaPairing target, CancellationToken token)
    {
        if (target.LibraryId == Guid.Empty || target.PeerId == Guid.Empty || !Enum.IsDefined(target.Kind) ||
            string.IsNullOrWhiteSpace(target.Name) || !ValidSegment(target.FolderId) || !ValidSegment(target.SenderDeviceId) ||
            !ValidSegment(target.ReceiverDeviceId) || target.SenderDeviceId == target.ReceiverDeviceId ||
            !SyncthingReceiverInventory.IsTrustedEndpoint(target.SenderEndpoint) || !SyncthingReceiverInventory.IsTrustedEndpoint(target.ReceiverEndpoint) ||
            target.SenderEndpoint == target.ReceiverEndpoint || !Path.IsPathFullyQualified(target.SenderRoot) ||
            !PcMirrorNativePath.IsDedicatedRoot(target.ReceiverRoot))
            return Reject("invalid-media-pairing-scope");

        try
        {
            if (!OrdinaryDirectory(target.SenderRoot))
                return Reject("pairing-sender-root-unavailable");
            var senderToken = await senders.GetTokenAsync(target.LibraryId, target.SenderDeviceId, token).ConfigureAwait(false);
            var receiverToken = await receivers.GetTokenAsync(target.LibraryId, target.PeerId, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(senderToken) || string.IsNullOrWhiteSpace(receiverToken))
                return Unavailable("pairing-credentials-unavailable");
            var sender = new Endpoint(FolderUri(target.SenderEndpoint), senderToken);
            var receiver = new Endpoint(FolderUri(target.ReceiverEndpoint), receiverToken);
            var senderIdentity = await ReadAsync(sender, "rest/system/status", token).ConfigureAwait(false);
            if (senderIdentity is not { } sourceIdentity)
                return Unavailable("pairing-sender-unavailable");
            if (GetString(sourceIdentity, "myID") != target.SenderDeviceId)
                return Reject("pairing-sender-identity-mismatch");
            var receiverIdentity = await ReadAsync(receiver, "rest/system/status", token).ConfigureAwait(false);
            if (receiverIdentity is not { } targetIdentity)
                return Unavailable("pairing-receiver-unavailable");
            if (GetString(targetIdentity, "myID") != target.ReceiverDeviceId)
                return Reject("pairing-receiver-identity-mismatch");

            var folderRoute = $"{Folders}/{Uri.EscapeDataString(target.FolderId)}";
            var receivingFolder = await ReadAsync(receiver, folderRoute, token, allowMissing: true).ConfigureAwait(false);
            if (receivingFolder is { } receiving && !MatchesReceiver(receiving, target))
                return Reject("pairing-receiver-folder-mismatch");
            var existingFolder = await ReadAsync(sender, folderRoute, token, allowMissing: true).ConfigureAwait(false);
            if (existingFolder is { } existing && !MatchesSender(existing, target))
                return Reject("pairing-sender-folder-mismatch");

            var deviceRoute = $"{Devices}/{Uri.EscapeDataString(target.ReceiverDeviceId)}";
            var knownDevice = await ReadAsync(sender, deviceRoute, token, allowMissing: true).ConfigureAwait(false);
            if (knownDevice is { } known && GetString(known, "deviceID") != target.ReceiverDeviceId)
                return Reject("pairing-sender-device-mismatch");
            if (knownDevice is null && !await EnrollAsync(sender, deviceRoute, target, token).ConfigureAwait(false))
                return Unavailable("pairing-sender-device-unverified");

            var desired = await EnsureFolderAsync(sender, folderRoute, target, token).ConfigureAwait(false);
            return desired switch
            {
                FolderOffer.Verified => new HostOutcome<MediaShareOffer>.Success(new(receivingFolder is not null)),
                FolderOffer.Conflict => Reject("pairing-sender-folder-mismatch"),
                _ => Unavailable("pairing-sender-folder-unverified")
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<MediaShareOffer>.Cancelled(); }
        catch (Exception error) when (SyncthingHostExportScanner.IsUnavailable(error)) { return Unavailable("pairing-native-unavailable"); }
    }

    private async Task<bool> EnrollAsync(Endpoint sender, string route, RegisteredMediaPairing target, CancellationToken token)
    {
        var device = await ReadAsync(sender, route, token, allowMissing: true).ConfigureAwait(false);
        if (device is { } alreadyConfigured)
            return GetString(alreadyConfigured, "deviceID") == target.ReceiverDeviceId;
        using var offered = await SendAsync(sender, HttpMethod.Post, Devices, JsonContent.Create(new
        {
            deviceID = target.ReceiverDeviceId, name = target.Name, addresses = new[] { "dynamic" },
            introducer = false, autoAcceptFolders = false
        }), token).ConfigureAwait(false);
        return offered.IsSuccessStatusCode && await ReadAsync(sender, route, token).ConfigureAwait(false) is { } verified &&
            GetString(verified, "deviceID") == target.ReceiverDeviceId;
    }

    private async Task<FolderOffer> EnsureFolderAsync(Endpoint sender, string folderRoute, RegisteredMediaPairing target, CancellationToken token)
    {
        var configuration = await ReadAsync(sender, folderRoute, token, allowMissing: true).ConfigureAwait(false);
        JsonElement[] previous = [];
        if (configuration is null)
        {
            using var created = await SendAsync(sender, HttpMethod.Post, Folders, JsonContent.Create(new
            {
                id = target.FolderId, label = target.FolderId, path = target.SenderRoot, type = "sendonly",
                devices = new[] { new { deviceID = target.SenderDeviceId }, new { deviceID = target.ReceiverDeviceId } }
            }), token).ConfigureAwait(false);
            if (!created.IsSuccessStatusCode)
                return FolderOffer.Unavailable;
        }
        else
        {
            if (!MatchesSender(configuration.Value, target))
                return FolderOffer.Conflict;
            previous = Members(configuration.Value)!;
            if (previous.Any(member => GetString(member, "deviceID") == target.ReceiverDeviceId))
                return FolderOffer.Verified;
            var joined = previous.Append(JsonSerializer.SerializeToElement(new { deviceID = target.ReceiverDeviceId }));
            using var patched = await SendAsync(sender, HttpMethod.Patch, folderRoute, JsonContent.Create(new { devices = joined }), token).ConfigureAwait(false);
            if (!patched.IsSuccessStatusCode)
                return FolderOffer.Unavailable;
        }

        var verified = await ReadAsync(sender, folderRoute, token).ConfigureAwait(false);
        return verified is { } actual && MatchesSender(actual, target) && Members(actual) is { } current &&
            current.Any(member => GetString(member, "deviceID") == target.ReceiverDeviceId) &&
            previous.All(prior => current.Any(member => JsonElement.DeepEquals(prior, member))) ? FolderOffer.Verified : FolderOffer.Unavailable;
    }

    private async Task<JsonElement?> ReadAsync(Endpoint endpoint, string route, CancellationToken token, bool allowMissing = false)
    {
        using var reply = await SendAsync(endpoint, HttpMethod.Get, route, null, token).ConfigureAwait(false);
        if (allowMissing && reply.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (reply.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("The native share configuration is unavailable.");
        var json = await reply.Content.ReadFromJsonAsync<JsonElement>(token).ConfigureAwait(false);
        return json.ValueKind == JsonValueKind.Object ? json : throw new JsonException("The native response must be a configuration object.");
    }

    private Task<HttpResponseMessage> SendAsync(Endpoint endpoint, HttpMethod method, string route, HttpContent? content, CancellationToken token) =>
        SyncthingHostExportScanner.SendAsync(client, endpoint.Root, method, route, endpoint.Credential, token, content);

    private static bool MatchesSender(JsonElement folder, RegisteredMediaPairing target)
    {
        if (GetString(folder, "id") != target.FolderId || GetString(folder, "type") != "sendonly" ||
            GetString(folder, "path") is not { } path || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.SenderRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || Members(folder) is not { } members)
            return false;
        var ids = members.Select(member => GetString(member, "deviceID")).ToHashSet(StringComparer.Ordinal);
        return ids.Contains(target.SenderDeviceId) && (target.Kind == MediaPeerType.PcMirror ||
            ids.All(id => id == target.SenderDeviceId || id == target.ReceiverDeviceId));
    }

    /// <summary>Uses the same approved-folder check as subsequent consent and index reads.</summary>
    /// <remarks>2026-11-24: Keep the native receiver scope consistent for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    private static bool MatchesReceiver(JsonElement folder, RegisteredMediaPairing target) =>
        SyncthingReceiverFolder.Matches(folder, target.FolderId, target.ReceiverRoot, target.SenderDeviceId, target.ReceiverDeviceId);

    private static JsonElement[]? Members(JsonElement folder)
    {
        if (!folder.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            return null;
        var members = devices.EnumerateArray().ToArray();
        var ids = members.Select(member => GetString(member, "deviceID")).ToArray();
        return ids.All(id => id is { Length: > 0 }) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length ? members : null;
    }

    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool OrdinaryDirectory(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || directory.LinkTarget is not null || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool ValidSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." && !value.Any(char.IsControl);

    private static Uri FolderUri(Uri address) => new(address.AbsoluteUri.TrimEnd('/') + '/');
    private static HostOutcome<MediaShareOffer> Reject(string code) => new HostOutcome<MediaShareOffer>.Rejected(code);
    private static HostOutcome<MediaShareOffer> Unavailable(string code) => new HostOutcome<MediaShareOffer>.Unavailable(code);

    private sealed record Endpoint(Uri Root, string Credential);
    private enum FolderOffer { Verified, Conflict, Unavailable }
}
