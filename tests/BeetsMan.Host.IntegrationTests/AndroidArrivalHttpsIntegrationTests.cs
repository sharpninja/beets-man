using System.Net;
using System.Net.Http.Headers;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using DesktopSync = BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Exercises the production event worker and desktop navigation through authenticated HTTPS.</summary>
/// <remarks>2026-11-24: Verify STB21 for FR-FILESYNC-001#ac-arrival without a GUI display.</remarks>
public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Checks native startup history, live Android transitions, gap recovery and desktop reconnect.</summary>
    [Fact]
    public async Task AndroidArrivalsSelectTheLivePeerWithoutReplayingHistoryOrInterruptingPlayback()
    {
        var dbPath = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
        var native = new AndroidNativeEventFixture();
        await using var nativeApp = native.Build();
        await nativeApp.StartAsync(Token);
        var nativeBase = new Uri(nativeApp.Urls.Single() + "/");
        var phone = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android", "phone", new(nativeBase, "phone/"), false);
        SeedHost(dbPath, [phone]);
        using (var database = new LiteDatabase(dbPath))
            database.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
            {
                ["_id"] = Guid.NewGuid(), ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active", ["SyncthingDeviceId"] = "desktop"
            });
        var options = new LibraryHostOptions(library, dbPath, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [new(phone.DeviceId, phone.ShareId, new(new(nativeBase, "sender/"), StartupSyncthingApi.SenderId, export))]);
        var identity = new Verifier(new(library, instance), secret, null);
        var credentials = new StartupSyncthingCredentials(library, phone.DeviceId, Guid.Empty);
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, socket => socket.UseHttps(certificate)));
        await using var host = LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        await host.StartAsync(Token);
        await native.WaitUntilAsync(() => native.RequestedAfter == 1, Token);
        var address = new Uri(host.Urls.Single());
        using var transport = new LostResponseHandler(CreateClientHandler(certificate));
        using var client = new HttpClient(transport) { BaseAddress = address };
        var adapter = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
        using var arrivals = new AndroidArrivalMonitor(adapter, instance);
        var window = Guid.NewGuid();
        var modal = new OverlayState(Guid.NewGuid(), Guid.NewGuid(), new OverlayIntent.ChoosePresentation(), "playlist-draft-name");
        var initial = new ShellState(window, 1, new(Workspace.Library), PresentationMode.FullVisualization,
            PresentationMode.BoundedVisualization, playback, [modal], "playlist-draft-name");
        var routes = new RequestDispatcher.Builder();
        new ShellHandlers(initial, arrivals).Register(routes);
        new DesktopSync.MediaMirrorHandlers(adapter).Register(routes);
        var services = new PresentationServices(routes.Build(), new InlineScheduler(), instance);
        using var shell = new ShellViewModel(services, window);
        using var media = new MediaSyncViewModel(services, Guid.NewGuid(), shell);
        using var player = new PlaybackViewModel(services, new PlaybackPresentation(playback));
        shell.Activate(window);
        media.Activate(media.Scope.OwnerId);
        player.Activate(playback);
        var playbackState = new PlaybackState(playback, 1, new(1, [], false), PlaybackCapabilities.Transport,
            TransportStatus.Playing, Guid.NewGuid(), TimeSpan.FromSeconds(102));
        await player.ReceiveAsync(playbackState);
        await shell.NavigateCommand.ExecuteAsync(new WorkspaceRoute(Workspace.Library));

        var history = await PollAsync();
        var historicalIgnored = history.Baseline && history.Connections is [{ Connected: true, ArrivedAtRevision: null }] &&
            shell.State?.Route.Workspace == Workspace.Library && media.SelectedPeerId is null;

        native.Add(2, "desktop", true);
        native.Add(3, "unregistered", true);
        native.Add(4, "phone", false);
        await native.WaitUntilAsync(() => native.RequestedAfter == 4, Token);
        var ignored = await PollAsync();
        var otherIgnored = ignored.Connections is [{ Connected: false, ArrivedAtRevision: null }] &&
            shell.State?.Route.Workspace == Workspace.Library && media.SelectedPeerId is null;

        native.Add(5, "phone", true);
        await native.WaitUntilAsync(() => native.RequestedAfter == 5, Token);
        var first = await PollAsync();
        Assert.NotNull(Assert.Single(first.Connections).ArrivedAtRevision);
        Assert.Equal(phone.DeviceId, media.SelectedPeerId);
        Assert.True(media.State!.Peers.Single(peer => peer.PeerId == phone.DeviceId).Connected);
        Assert.Equal(phone.DeviceId, shell.State?.Route.PeerId);
        Assert.Equal(playbackState, player.State);
        await player.ReceiveAsync(playbackState with { Revision = 2, Position = TimeSpan.FromSeconds(104) });
        Assert.Equal(TimeSpan.FromSeconds(104), player.State!.Position);
        var preserved = shell.State!.Overlays.SequenceEqual([modal]) && shell.State.FocusTarget == "playlist-draft-name" &&
            shell.State.PlaybackSessionId == playback && player.State.ActiveTrackId == playbackState.ActiveTrackId;

        await shell.NavigateCommand.ExecuteAsync(new WorkspaceRoute(Workspace.Library));
        native.EchoLastEvent();
        await native.WaitUntilAsync(() => native.Echoed > 0, Token);
        await PollAsync();
        var duplicateIgnored = shell.State?.Route.Workspace == Workspace.Library;

        native.Add(6, "phone", false);
        await native.WaitUntilAsync(() => native.RequestedAfter == 6, Token);
        await PollAsync();
        native.Add(9, "phone", true);
        await native.WaitUntilAsync(() => native.RequestedAfter == 9, Token);
        var reconciled = await PollAsync();
        var gapSafe = reconciled.Connections is [{ Connected: true, ArrivedAtRevision: null }] && shell.State?.Route.Workspace == Workspace.Library;

        native.Add(10, "phone", false);
        native.Add(11, "phone", true);
        await native.WaitUntilAsync(() => native.RequestedAfter == 11, Token);
        await PollAsync();
        var selected = shell.State!;
        var fresh = selected.Route.Workspace == Workspace.Sync && media.SelectedPeerId == phone.DeviceId;

        await shell.NavigateCommand.ExecuteAsync(new WorkspaceRoute(Workspace.Library));
        transport.DropNextSuccess = true;
        var lost = await arrivals.PollOnceAsync(shell, media, Token);
        native.Add(12, "phone", false);
        native.Add(13, "phone", true);
        await native.WaitUntilAsync(() => native.RequestedAfter == 13, Token);
        var reopened = await PollAsync();
        var reconnectSilent = lost is Outcome<DesktopSync.AndroidArrivalFrame>.DependencyFailure && reopened.Baseline &&
            reopened.Connections is [{ Connected: true, ArrivedAtRevision: null }] && shell.State?.Route.Workspace == Workspace.Library;

        native.Restart();
        await native.WaitUntilAsync(() => native.RequestedAfter == 1, Token);
        var epochReset = await PollAsync();
        gapSafe &= epochReset.Connections is [{ Connected: true, ArrivedAtRevision: null }] && shell.State?.Route.Workspace == Workspace.Library;
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, AndroidArrivalRoute.ReadPath(Guid.NewGuid()));
        foreignRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var foreign = await client.SendAsync(foreignRequest, Token);

        StageAssertions.VerifyAndroidArrival(new(historicalIgnored, duplicateIgnored, otherIgnored, gapSafe, fresh,
            selected.Mode.ToString(), selected.Route.PeerId!.Value, preserved, (int)foreign.StatusCode, reconnectSilent), phone.DeviceId);

        async Task<DesktopSync.AndroidArrivalFrame> PollAsync() =>
            Assert.IsType<Outcome<DesktopSync.AndroidArrivalFrame>.Success>(await arrivals.PollOnceAsync(shell, media, Token)).Value;
    }

    private sealed class AndroidNativeEventFixture
    {
        private const string Topic = "DeviceConnected,DeviceDisconnected";
        private readonly object gate = new();
        private readonly List<Event> history = [new(1, "phone", true)];
        private readonly Dictionary<string, bool> connections = new(StringComparer.Ordinal) { ["phone"] = true };
        private DateTimeOffset started = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        private bool echo;
        private int echoed;
        private long requestedAfter = -1;
        public long RequestedAfter => Interlocked.Read(ref requestedAfter);
        public int Echoed => Volatile.Read(ref echoed);

        public void Add(long id, string device, bool connected)
        {
            lock (gate)
            {
                connections[device] = connected;
                history.Add(new(id, device, connected));
            }
        }

        public void EchoLastEvent()
        {
            lock (gate)
                echo = true;
        }

        public void Restart()
        {
            lock (gate)
            {
                Interlocked.Exchange(ref requestedAfter, -1);
                started = started.AddMinutes(1);
                history.Clear();
                history.Add(new(1, "phone", true));
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
        {
            for (var attempt = 0; attempt < 300 && !condition(); attempt++)
                await Task.Delay(20, token);
            Assert.True(condition(), $"The arrival worker did not reach the expected native cursor. Last cursor was {RequestedAfter}.");
        }

        public WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapGet("/sender/rest/system/status", (HttpContext context) =>
            {
                if (!Authorized(context))
                    return Results.Unauthorized();
                lock (gate)
                    return Results.Json(new { myID = StartupSyncthingApi.SenderId, startTime = started });
            });
            app.MapGet("/sender/rest/system/connections", (HttpContext context) =>
            {
                if (!Authorized(context))
                    return Results.Unauthorized();
                lock (gate)
                    return Results.Json(new { connections = connections.ToDictionary(pair => pair.Key, pair => new { connected = pair.Value }) });
            });
            app.MapGet("/sender/rest/events", async (string events, long since, int? limit, HttpContext context) =>
            {
                if (!Authorized(context) || events != Topic)
                    return Results.Unauthorized();
                if (limit is not null && (limit != 1 || since != 0))
                    return Results.BadRequest();
                if (limit is null)
                {
                    Interlocked.Exchange(ref requestedAfter, since);
                    await Task.Delay(20, context.RequestAborted);
                }
                lock (gate)
                {
                    Event[] batch;
                    if (limit == 1)
                        batch = [history[^1]];
                    else if (echo)
                    {
                        echo = false;
                        Interlocked.Increment(ref echoed);
                        batch = [history[^1]];
                    }
                    else
                        batch = history.Where(item => item.Id > since).ToArray();
                    return Results.Json(batch.Select(item => new
                    {
                        id = item.Id, globalID = item.Id * 4, type = item.Connected ? "DeviceConnected" : "DeviceDisconnected",
                        data = new { id = item.Device }
                    }));
                }
            });
            return app;
        }

        private static bool Authorized(HttpContext context) => context.Request.Headers.Authorization == "Bearer fixture-sync-sender";

        private sealed record Event(long Id, string Device, bool Connected);
    }
}
