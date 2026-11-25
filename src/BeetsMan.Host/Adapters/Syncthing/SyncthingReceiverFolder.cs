using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Verifies that the selected receiver accepted the approved folder and sender.</summary>
/// <remarks>2026-11-24: Share the receiver boundary for FR-FILESYNC-001#ac-layouts, ac-playlist-drop, and ac-safety.</remarks>
internal static class SyncthingReceiverFolder
{
    /// <summary>Reads the folder condition after the caller has verified the receiver's native identity.</summary>
    internal static async Task<HostOutcome<AndroidShareCondition>> ReadAsync(HttpClient client, Uri endpoint, string credential,
        SyncthingReceiver receiver, CancellationToken token)
    {
        if (receiver.ApprovedRoot is not { } root || !PcMirrorNativePath.IsDedicatedRoot(root) ||
            string.IsNullOrWhiteSpace(receiver.ApprovedSenderId) || receiver.ApprovedSenderId == receiver.DeviceId)
            return new HostOutcome<AndroidShareCondition>.Rejected("export-receiver-folder-scope-mismatch");
        using var response = await SyncthingHostExportScanner.SendAsync(client, endpoint, HttpMethod.Get,
            $"rest/config/folders/{Uri.EscapeDataString(receiver.FolderId)}", credential, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new HostOutcome<AndroidShareCondition>.Success(AndroidShareCondition.AwaitingAcceptance);
        if (response.StatusCode != HttpStatusCode.OK)
            return new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-folder-unavailable");
        var folder = await response.Content.ReadFromJsonAsync<JsonElement>(token).ConfigureAwait(false);
        if (!Matches(folder, receiver.FolderId, root, receiver.ApprovedSenderId, receiver.DeviceId))
            return new HostOutcome<AndroidShareCondition>.Rejected("export-receiver-folder-scope-mismatch");
        if (!folder.TryGetProperty("paused", out var paused) || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-folder-unavailable");
        return new HostOutcome<AndroidShareCondition>.Success(paused.GetBoolean() ? AndroidShareCondition.Paused : AndroidShareCondition.Ready);
    }

    /// <summary>Requires the approved folder to be ready before using an index read as evidence.</summary>
    internal static async Task<HostOutcome<bool>> RequireReadyAsync(HttpClient client, Uri endpoint, string credential,
        SyncthingReceiver receiver, CancellationToken token)
    {
        if (receiver.ApprovedRoot is null)
            return new HostOutcome<bool>.Success(true);
        var status = await ReadAsync(client, endpoint, credential, receiver, token).ConfigureAwait(false);
        return status switch
        {
            HostOutcome<AndroidShareCondition>.Success { Value: AndroidShareCondition.Ready } => new HostOutcome<bool>.Success(true),
            HostOutcome<AndroidShareCondition>.Success { Value: AndroidShareCondition.Paused } => new HostOutcome<bool>.Unavailable("export-receiver-folder-paused"),
            HostOutcome<AndroidShareCondition>.Success => new HostOutcome<bool>.Unavailable("export-receiver-awaiting-acceptance"),
            _ => status.PropagateFailure<bool>()
        };
    }

    /// <summary>Checks the receiver's exact path, read-only direction and two approved members.</summary>
    internal static bool Matches(JsonElement folder, string id, string root, string sender, string receiver)
    {
        if (ReadString(folder, "id") != id || ReadString(folder, "type") != "receiveonly" ||
            ReadString(folder, "path") is not { } path || !PcMirrorNativePath.Matches(path, root) ||
            !folder.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() != 2)
            return false;
        return devices.EnumerateArray().Select(device => ReadString(device, "deviceID")).ToHashSet(StringComparer.Ordinal).SetEquals([sender, receiver]);
    }

    private static string? ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
