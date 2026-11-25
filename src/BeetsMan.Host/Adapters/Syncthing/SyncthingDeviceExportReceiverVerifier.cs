using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Verifies reviewed export paths against stable, scoped sender and receiver indexes.</summary>
/// <remarks>2026-11-24: Verify the published versions, including removals, for FR-FILESYNC-001#ac-safety and TEST-FILESYNC-002.</remarks>
public sealed class SyncthingDeviceExportReceiverVerifier(HttpClient client, SyncthingHostExportScanner sender,
    IRegisteredSyncthingTokenProvider receiverCredentials, ISyncthingExportSenderRegistry? senders = null) : IDeviceExportReceiverVerifier
{
    private const string NotIndexed = "No such object in the index";

    /// <inheritdoc />
    public async Task<HostOutcome<ImmutableArray<DeviceExportTransferItem>>> CaptureSenderAsync(RegisteredAndroidExport target,
        ImmutableArray<DeviceExportTransferItem> items, CancellationToken token)
    {
        var opened = await sender.OpenSenderAsync(target, token).ConfigureAwait(false);
        if (opened is not HostOutcome<SyncthingHostExportScanner.SenderConnection>.Success { Value: var source })
            return opened.PropagateFailure<ImmutableArray<DeviceExportTransferItem>>();
        var paths = items.Select(item => item.Path).ToArray();
        var share = await new FileDeviceExportSnapshotReader(source.ExportRoot, target).ReadAsync(target, paths, token).ConfigureAwait(false);
        if (share is not HostOutcome<DeviceExportShareSnapshot>.Success { Value: var observed })
            return share.PropagateFailure<ImmutableArray<DeviceExportTransferItem>>();
        var comparer = target.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var byPath = observed.Paths.ToDictionary(item => item.Path, comparer);
        foreach (var item in items)
        {
            byPath.TryGetValue(item.Path, out var file);
            if (item.ContentIdentity is null ? file is not null : file is not { Kind: DeviceExportPathKind.File } ||
                !string.Equals(file.ContentIdentity, item.ContentIdentity, StringComparison.OrdinalIgnoreCase))
                return new HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Rejected("export-sender-file-changed");
        }

        var snapshot = await ReadIndexAsync(source.Endpoint, target.ShareId, source.Credential, paths, token).ConfigureAwait(false);
        if (snapshot is not HostOutcome<ImmutableArray<IndexedExportFile>>.Success { Value: var indexed })
            return snapshot.PropagateFailure<ImmutableArray<DeviceExportTransferItem>>();
        var result = ImmutableArray.CreateBuilder<DeviceExportTransferItem>(items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var entry = indexed[index];
            if (item.ContentIdentity is null ? entry.Present : !entry.Present || entry.Bytes != byPath[item.Path].Length)
                return new HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Unavailable("export-sender-index-pending");
            result.Add(item with { Bytes = entry.Bytes, Version = entry.Version });
        }
        return new HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Success(result.MoveToImmutable());
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Verify ready consent and exact receiver scope before and after the index for FR-FILESYNC-001#ac-safety.</remarks>
    public async Task<HostOutcome<DeviceExportReceiverProgress>> ObserveReceiverAsync(RegisteredAndroidExport target,
        ImmutableArray<DeviceExportTransferItem> items, CancellationToken token)
    {
        if (!SyncthingReceiverInventory.IsTrustedEndpoint(target.ReceiverEndpoint) || string.IsNullOrWhiteSpace(target.SyncthingDeviceId))
            return new HostOutcome<DeviceExportReceiverProgress>.Rejected("export-receiver-registration-unavailable");
        if (items.IsDefaultOrEmpty || items.Any(item => item.Bytes is not >= 0 || item.Version is null ||
            item.ContentIdentity is not null && item.Version.Length == 0))
            return new HostOutcome<DeviceExportReceiverProgress>.Rejected("invalid-export-sender-proof");

        try
        {
            var credential = await receiverCredentials.GetTokenAsync(target.LibraryId, target.DeviceId, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
                return new HostOutcome<DeviceExportReceiverProgress>.Unavailable("export-receiver-credentials-unavailable");
            var endpoint = new Uri(target.ReceiverEndpoint.AbsoluteUri.TrimEnd('/') + '/');
            using var reply = await GetAsync(endpoint, "rest/system/status", credential, token).ConfigureAwait(false);
            if (reply.StatusCode != HttpStatusCode.OK)
                return new HostOutcome<DeviceExportReceiverProgress>.Unavailable("export-receiver-unavailable");
            var receiver = await reply.Content.ReadFromJsonAsync<ReceiverIdentity>(token).ConfigureAwait(false);
            if (receiver?.MyId != target.SyncthingDeviceId)
                return new HostOutcome<DeviceExportReceiverProgress>.Rejected("export-receiver-device-mismatch");

            var scope = SyncthingReceiver.FromRegistration(target, senders);
            var folderReady = await SyncthingReceiverFolder.RequireReadyAsync(client, endpoint, credential, scope, token).ConfigureAwait(false);
            if (folderReady is not HostOutcome<bool>.Success)
                return folderReady.PropagateFailure<DeviceExportReceiverProgress>();

            var snapshot = await ReadIndexAsync(endpoint, target.ShareId, credential, items.Select(item => item.Path).ToArray(), token).ConfigureAwait(false);
            if (snapshot is not HostOutcome<ImmutableArray<IndexedExportFile>>.Success { Value: var indexed })
                return snapshot.PropagateFailure<DeviceExportReceiverProgress>();
            folderReady = await SyncthingReceiverFolder.RequireReadyAsync(client, endpoint, credential, scope, token).ConfigureAwait(false);
            if (folderReady is not HostOutcome<bool>.Success)
                return folderReady.PropagateFailure<DeviceExportReceiverProgress>();
            long verifiedBytes = 0, totalBytes = 0;
            var allReceived = true;
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var entry = indexed[index];
                if (item.ContentIdentity is null)
                {
                    allReceived &= !entry.Present && (item.Version!.Length == 0 || item.Version == entry.Version);
                    continue;
                }
                totalBytes = checked(totalBytes + item.Bytes!.Value);
                if (!entry.Present || item.Version != entry.Version || item.Bytes != entry.Bytes)
                    allReceived = false;
                else
                    verifiedBytes = checked(verifiedBytes + entry.Bytes);
            }
            return new HostOutcome<DeviceExportReceiverProgress>.Success(new(verifiedBytes, totalBytes, allReceived));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<DeviceExportReceiverProgress>.Cancelled(); }
        catch (Exception exception) when (SyncthingHostExportScanner.IsUnavailable(exception) || exception is OverflowException)
        { return new HostOutcome<DeviceExportReceiverProgress>.Unavailable("export-receiver-unavailable"); }
    }

    private async Task<HostOutcome<ImmutableArray<IndexedExportFile>>> ReadIndexAsync(Uri endpoint, string shareId, string credential,
        string[] paths, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(shareId) || paths.Any(path => !PlaylistExportPlanner.TryNormalize(path, out var normalized) || path != normalized))
            return new HostOutcome<ImmutableArray<IndexedExportFile>>.Rejected("invalid-export-index-path");
        try
        {
            var folder = Uri.EscapeDataString(shareId);
            var statusRoute = $"rest/db/status?folder={folder}";
            var before = await ReadStatusAsync(endpoint, statusRoute, credential, token).ConfigureAwait(false);
            if (before is not { State: "idle", IgnorePatterns: not null, Sequence: not null, StateChanged: not null })
                return new HostOutcome<ImmutableArray<IndexedExportFile>>.Unavailable("export-index-not-ready");

            var items = ImmutableArray.CreateBuilder<IndexedExportFile>(paths.Length);
            foreach (var path in paths)
            {
                using var reply = await GetAsync(endpoint, $"rest/db/file?folder={folder}&file={Uri.EscapeDataString(path)}", credential, token)
                    .ConfigureAwait(false);
                var item = await ReadFileAsync(reply, path, before.IgnorePatterns == false, token).ConfigureAwait(false);
                if (item is null)
                    return new HostOutcome<ImmutableArray<IndexedExportFile>>.Unavailable("export-index-file-unavailable");
                items.Add(item);
            }
            var after = await ReadStatusAsync(endpoint, statusRoute, credential, token).ConfigureAwait(false);
            return before == after ? new HostOutcome<ImmutableArray<IndexedExportFile>>.Success(items.MoveToImmutable()) :
                new HostOutcome<ImmutableArray<IndexedExportFile>>.Unavailable("export-index-changed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<ImmutableArray<IndexedExportFile>>.Cancelled(); }
        catch (Exception exception) when (SyncthingHostExportScanner.IsUnavailable(exception))
        { return new HostOutcome<ImmutableArray<IndexedExportFile>>.Unavailable("export-index-unavailable"); }
    }

    private async Task<FolderStatus?> ReadStatusAsync(Uri endpoint, string route, string credential, CancellationToken token)
    {
        using var result = await GetAsync(endpoint, route, credential, token).ConfigureAwait(false);
        return result.StatusCode == HttpStatusCode.OK ? await result.Content.ReadFromJsonAsync<FolderStatus>(token).ConfigureAwait(false) : null;
    }

    private static async Task<IndexedExportFile?> ReadFileAsync(HttpResponseMessage response, string path, bool canInferAbsence, CancellationToken token)
    {
        if (canInferAbsence && response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType == "text/plain" &&
            (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).TrimEnd('\r', '\n') == NotIndexed)
            return new(false, 0, string.Empty);
        if (response.StatusCode != HttpStatusCode.OK)
            return null;
        var snapshot = await response.Content.ReadFromJsonAsync<IndexedFileReply>(token).ConfigureAwait(false);
        var local = snapshot?.Local;
        if (canInferAbsence && local is { Name: "", Deleted: false, Invalid: false, Ignored: false, MustRescan: false } && snapshot?.Global?.Name == path)
        {
            var tombstone = snapshot.Global.Deleted == true ? VersionOf(snapshot.Global.Version) : null;
            return new(false, 0, tombstone ?? string.Empty);
        }
        if (local is not { Invalid: false, Ignored: false, MustRescan: false, Deleted: not null } || local.Name != path ||
            VersionOf(local.Version) is not { } version || !IsFile(local.Type))
            return null;
        if (local.Deleted == true)
            return new(false, 0, version);
        return local.Size is >= 0 ? new(true, local.Size.Value, version) : null;
    }

    private static string? VersionOf(string[]? versions) => versions is { Length: > 0 } &&
        versions.All(version => !string.IsNullOrWhiteSpace(version) && !version.Any(char.IsControl))
        ? string.Join('\n', versions.Order(StringComparer.Ordinal)) : null;

    private static bool IsFile(JsonElement type) => type.ValueKind == JsonValueKind.String && type.GetString() == "FILE_INFO_TYPE_FILE" ||
        type.ValueKind == JsonValueKind.Number && type.TryGetInt32(out var number) && number == 0;

    private Task<HttpResponseMessage> GetAsync(Uri endpoint, string path, string credential, CancellationToken token) =>
        SyncthingHostExportScanner.SendAsync(client, endpoint, HttpMethod.Get, path, credential, token);

    private sealed record FolderStatus(string? State, long? Sequence, DateTimeOffset? StateChanged, bool? IgnorePatterns);
    private sealed record ReceiverIdentity(string? MyId);
    private sealed record IndexedExportFile(bool Present, long Bytes, string Version);
    private sealed record IndexedFileReply(IndexEntry? Local, IndexEntry? Global);
    private sealed record IndexEntry(string? Name, bool? Deleted, bool? Invalid, bool? Ignored, bool? MustRescan, long? Size, JsonElement Type, string[]? Version);
}
