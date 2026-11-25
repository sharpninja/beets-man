using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using Microsoft.Extensions.Hosting;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Observes trusted sender connections and relays only continuous, confirmed Android arrivals.</summary>
/// <remarks>2026-11-24: Implement STB21 stream baselines, deduplication and gap recovery for FR-FILESYNC-001#ac-arrival.</remarks>
internal sealed class SyncthingAndroidArrivalWorker(LibraryHostOptions options, LiteDbHostPlaylistStore store,
    AndroidArrivalHub hub, SyncthingAndroidEvents native) : BackgroundService
{
    private readonly ImmutableDictionary<AndroidArrivalSender, ImmutableDictionary<Guid, string>> sources = options.AndroidExports
        .GroupBy(route => new AndroidArrivalSender(new(route.Sender.Endpoint.AbsoluteUri.TrimEnd('/') + '/'), route.Sender.DeviceId))
        .ToImmutableDictionary(group => group.Key, group => group.ToImmutableDictionary(route => route.DeviceId, route => route.FolderId));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(sources.Select(source => WatchAsync(source.Key, source.Value, stoppingToken)));

    private async Task WatchAsync(AndroidArrivalSender sender, ImmutableDictionary<Guid, string> routes, CancellationToken token)
    {
        SourceState? state = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                state = await RefreshAsync(sender, routes, state, token).ConfigureAwait(false);
                if (state is not null)
                    continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (SyncthingHostExportScanner.IsUnavailable(error)) { state = null; }

            hub.Invalidate(sender);
            await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
        }
    }

    private async Task<SourceState?> RefreshAsync(AndroidArrivalSender sender, ImmutableDictionary<Guid, string> routes,
        SourceState? previous, CancellationToken token)
    {
        var read = await store.ReadAndroidArrivalsAsync(options.LibraryId, null, token).ConfigureAwait(false);
        if (read is not HostOutcome<ImmutableArray<RegisteredAndroidExport>>.Success { Value: var registered })
            return null;
        ImmutableArray<RegisteredAndroidExport> targets = [.. registered
            .Where(target => routes.GetValueOrDefault(target.DeviceId) == target.ShareId && target.SyncthingDeviceId != sender.DeviceId)
            .GroupBy(target => target.SyncthingDeviceId, StringComparer.Ordinal).Where(group => group.Count() == 1).SelectMany(group => group)];
        if (targets.IsEmpty)
            return null;
        var tokenValue = await native.GetCredentialAsync(options.LibraryId, sender.DeviceId, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokenValue))
            return null;
        var epoch = await native.ReadStartedAsync(sender, tokenValue, token).ConfigureAwait(false);
        if (epoch is null)
            return null;
        if (previous is null || previous.Started != epoch || !previous.Targets.SequenceEqual(targets))
        {
            hub.Invalidate(sender);
            return await BaselineAsync(sender, tokenValue, epoch.Value, targets, token).ConfigureAwait(false);
        }

        var batch = await native.ReadEventsAsync(sender, tokenValue, previous.Cursor, token).ConfigureAwait(false);
        if (batch is null)
            return null;
        var newEvents = batch.Where(item => item.Id > previous.Cursor).ToArray();
        var nextCursor = previous.Cursor;
        foreach (var item in newEvents)
        {
            if (item.Id != checked(nextCursor + 1))
            {
                hub.Invalidate(sender);
                return await BaselineAsync(sender, tokenValue, epoch.Value, targets, token).ConfigureAwait(false);
            }
            nextCursor = item.Id;
        }
        if (newEvents.Length == 0)
            return previous;

        var nativeTargets = targets.Select(target => target.SyncthingDeviceId).ToHashSet(StringComparer.Ordinal);
        var relevant = newEvents.Where(item => item.DeviceId is not null && nativeTargets.Contains(item.DeviceId)).ToArray();
        if (relevant.Length == 0)
            return previous with { Cursor = nextCursor };
        var current = await native.ReadConnectionsAsync(sender, tokenValue, token).ConfigureAwait(false);
        if (current is null || await native.ReadStartedAsync(sender, tokenValue, token).ConfigureAwait(false) != epoch)
            return null;

        var simulated = previous.Connections.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var arrivals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in relevant)
        {
            var id = item.DeviceId!;
            var connected = item.Type == "DeviceConnected";
            if (!connected)
                arrivals.Remove(id);
            else if (!simulated.GetValueOrDefault(id) && current.GetValueOrDefault(id))
                arrivals.Add(id);
            simulated[id] = connected;
        }
        hub.Publish(sender, targets, current, arrivals);
        return new(epoch.Value, nextCursor, targets, current);
    }

    private async Task<SourceState?> BaselineAsync(AndroidArrivalSender sender, string credential, DateTimeOffset epoch,
        ImmutableArray<RegisteredAndroidExport> targets, CancellationToken token)
    {
        var latest = await native.ReadEventsAsync(sender, credential, null, token).ConfigureAwait(false);
        var connected = await native.ReadConnectionsAsync(sender, credential, token).ConfigureAwait(false);
        if (latest is null || connected is null || await native.ReadStartedAsync(sender, credential, token).ConfigureAwait(false) != epoch)
            return null;
        hub.Publish(sender, targets, connected, null);
        return new(epoch, latest.LastOrDefault()?.Id ?? 0, targets, connected);
    }

    private sealed record SourceState(DateTimeOffset Started, long Cursor, ImmutableArray<RegisteredAndroidExport> Targets,
        IReadOnlyDictionary<string, bool> Connections);
}

/// <summary>Reads the connection stream and current state from precisely the configured Syncthing sender.</summary>
internal sealed class SyncthingAndroidEvents(HttpClient client, IHostSenderTokenProvider credentials)
{
    public ValueTask<string?> GetCredentialAsync(Guid libraryId, string senderId, CancellationToken token) =>
        credentials.GetTokenAsync(libraryId, senderId, token);

    public async Task<DateTimeOffset?> ReadStartedAsync(AndroidArrivalSender sender, string credential, CancellationToken token)
    {
        var status = await ReadAsync<NativeStatus>(sender, credential, "rest/system/status", token).ConfigureAwait(false);
        return status is { StartTime: { } start } && status.DeviceId == sender.DeviceId && start != default ? start : null;
    }

    public async Task<NativeEvent[]?> ReadEventsAsync(AndroidArrivalSender sender, string credential, long? after, CancellationToken token)
    {
        const string topic = "rest/events?events=DeviceConnected,DeviceDisconnected";
        var route = after is null ? $"{topic}&since=0&limit=1&timeout=1" :
            $"{topic}&since={after.Value.ToString(CultureInfo.InvariantCulture)}&timeout=3";
        var batch = await ReadAsync<EventPayload[]>(sender, credential, route, token).ConfigureAwait(false);
        if (batch is null || batch.Any(item => item is not { Id: > 0, Type: "DeviceConnected" or "DeviceDisconnected", Data.DeviceId.Length: > 0 }))
            return null;
        return [.. batch.Select(item => new NativeEvent(item.Id, item.Type!, item.Data!.DeviceId))];
    }

    public async Task<IReadOnlyDictionary<string, bool>?> ReadConnectionsAsync(AndroidArrivalSender sender, string credential, CancellationToken token)
    {
        var status = await ReadAsync<NativeConnections>(sender, credential, "rest/system/connections", token).ConfigureAwait(false);
        return status?.Connections?.ToDictionary(pair => pair.Key, pair => pair.Value?.Connected == true, StringComparer.Ordinal);
    }

    private async Task<T?> ReadAsync<T>(AndroidArrivalSender sender, string credential, string route, CancellationToken token) where T : class
    {
        using var response = await SyncthingHostExportScanner.SendAsync(client, sender.Endpoint, HttpMethod.Get, route, credential, token).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<T>(token).ConfigureAwait(false) : null;
    }

    internal sealed record NativeEvent(long Id, string Type, string? DeviceId);
    private sealed record NativeStatus([property: JsonPropertyName("myID")] string? DeviceId, DateTimeOffset? StartTime);
    private sealed record EventPayload(long Id, string? Type, NativeDevice? Data);
    private sealed record NativeDevice([property: JsonPropertyName("id")] string? DeviceId);
    private sealed record NativeConnections(Dictionary<string, NativeConnection?>? Connections);
    private sealed record NativeConnection(bool Connected);
}
