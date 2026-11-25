using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Syncthing;
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

/// <summary>Exercises registered PC mirrors through the real desktop and HTTPS host.</summary>
/// <remarks>2026-11-24: Verify STB10 for FR-FILESYNC-001#ac-mirror across both response boundaries and host restart.</remarks>
public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies a selected mirror's durable progress and scoped control after both native and HTTPS acknowledgements are lost.</summary>
    [Fact]
    public async Task PcMirrorCommandsRecoverBothLostAcknowledgementsAndKeepTheOtherShareUnchanged()
    {
        var database = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var native = new MirrorNativeApi(music);
        await using var nativeApp = native.Build();
        await nativeApp.StartAsync(Token);
        var nativeBase = new Uri(nativeApp.Urls.Single() + "/");
        var studio = Guid.NewGuid();
        var travel = Guid.NewGuid();
        var unconfigured = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var android = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android", "android", new(nativeBase, "android/"), false);
        SeedHost(database, [android], revoked);
        using (var db = new LiteDatabase(database))
        {
            AddPeer(studio, library, "Studio PC", "studio");
            AddPeer(travel, library, "Travel laptop", "travel");
            AddPeer(unconfigured, library, "Unconfigured", "unconfigured");
            AddPeer(foreign, Guid.NewGuid(), "Foreign", "foreign");

            void AddPeer(Guid id, Guid owner, string name, string nativeId) => db.GetCollection<BsonDocument>("registered_instances")
                .Insert(new BsonDocument { ["_id"] = id, ["LibraryId"] = owner, ["Role"] = "Desktop", ["Status"] = "Active",
                    ["Name"] = name, ["SyncthingDeviceId"] = nativeId });
        }
        var options = new LibraryHostOptions(library, database, music, Path.Combine(root, "stage"), Path.Combine(root, "work"), [],
            PcMirrors: [Configure(studio, "studio", "D:/Media/Music"), Configure(travel, "travel", "/mnt/music"),
                Configure(foreign, "foreign", "/foreign/music")]);
        var credentials = new MirrorNativeCredentials(library, studio, travel);
        var identity = new Verifier(new(library, instance), secret, revoked);
        using var certificate = CreateCertificate();
        var operation = new CommandContext(new(Guid.NewGuid(), instance), Guid.NewGuid());
        DesktopSync.MediaShare baseline, travelBaseline, afterPause, afterResume, travelAfter;
        DesktopSync.SetMirrorPaused pause;
        Outcome<DesktopSync.MediaSyncState> nativeLoss, responseLoss;
        bool revokedDenied, unknownHidden, exactReplay, readPaused;
        int foreignStatus;
        string staleCode, changedIntent;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var wire = new LostResponseHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(wire) { BaseAddress = address };
            var adapter = Gateway(client, address);
            var dispatch = Dispatcher(adapter);
            var initial = Value(await dispatch.QueryAsync(new DesktopSync.GetMediaSyncState(), operation.Request, Token));
            baseline = initial.Shares.Single(share => share.PeerId == studio);
            travelBaseline = initial.Shares.Single(share => share.PeerId == travel);
            var phone = initial.Peers.Single(peer => peer.PeerId == android.DeviceId);
            Assert.Equal(DesktopSync.MediaPeerKind.Android, phone.Kind);
            Assert.False(phone.ConnectionKnown);
            unknownHidden = initial.Peers.All(peer => peer.PeerId != unconfigured && peer.PeerId != foreign);
            Assert.All(initial.Peers.Where(peer => peer.Kind == DesktopSync.MediaPeerKind.PcMirror),
                peer => { Assert.True(peer.Connected); Assert.True(peer.ConnectionKnown); Assert.Equal(1, peer.ConnectionGeneration); });
            using (var request = new HttpRequestMessage(HttpMethod.Get, MediaMirrorRoutes.ReadPath(Guid.NewGuid())))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using var reply = await client.SendAsync(request, Token);
                foreignStatus = (int)reply.StatusCode;
            }
            var revokedGateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, revoked, RevokedCredential));
            revokedDenied = await revokedGateway.ReadMediaAsync(new(Guid.NewGuid(), revoked), Token) is Outcome<DesktopSync.MediaSyncState>.Invalid;

            pause = new DesktopSync.SetMirrorPaused(baseline.ShareId, true, baseline.Revision);
            native.LoseFirstPauseReply = true;
            nativeLoss = await dispatch.SendAsync(pause, operation, Token);
            Assert.True(native.StudioPaused);
            wire.DropNextSuccess = true;
            responseLoss = await dispatch.SendAsync(pause, operation, Token);
        }
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            var dispatch = Dispatcher(Gateway(client, address));
            var replay = Value(await dispatch.SendAsync(pause, operation, Token));
            afterPause = replay.Shares.Single(share => share.PeerId == studio);
            var replayAgain = Value(await dispatch.SendAsync(pause, operation, Token));
            exactReplay = nativeLoss is Outcome<DesktopSync.MediaSyncState>.DependencyFailure { Code: "mirror-pause-unavailable", Retryable: true } &&
                responseLoss is Outcome<DesktopSync.MediaSyncState>.DependencyFailure { Code: "media-mirror-control-host-unreachable", Retryable: true } &&
                afterPause == replayAgain.Shares.Single(share => share.PeerId == studio) && replay.Revision == replayAgain.Revision;
            changedIntent = Assert.IsType<Outcome<DesktopSync.MediaSyncState>.Conflict>(
                await dispatch.SendAsync(pause with { Paused = false }, operation, Token)).Code;
            staleCode = Assert.IsType<Outcome<DesktopSync.MediaSyncState>.Conflict>(
                await dispatch.SendAsync(pause with { Paused = false }, operation with { OperationId = Guid.NewGuid() }, Token)).Code;

            var window = Guid.NewGuid();
            var presentation = new PresentationServices(dispatch, new InlineScheduler(), instance);
            using var shell = new ShellViewModel(presentation, window);
            using var viewModel = new MediaSyncViewModel(presentation, Guid.NewGuid(), shell);
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenMirrorsCommand.ExecuteAsync(null);
            var pausedScope = viewModel.State!.Shares.Single(share => share.PeerId == studio);
            readPaused = pausedScope == afterPause && pausedScope.Paused;
            await viewModel.ResumeMirrorCommand.ExecuteAsync(pausedScope);
            Assert.Null(viewModel.Failure);
            afterResume = viewModel.State!.Shares.Single(share => share.PeerId == studio);
            Assert.False(afterResume.Paused);
            await viewModel.RescanCommand.ExecuteAsync(afterResume.ShareId);
            Assert.Null(viewModel.Failure);
            travelAfter = viewModel.State!.Shares.Single(share => share.PeerId == travel);
        }
        StageAssertions.VerifyPcMirrorCommands(new([baseline.Progress!.Phase.ToString(), afterPause.Progress!.Phase.ToString(),
            afterResume.Progress!.Phase.ToString()], [baseline.Progress.FilesReceived, afterPause.Progress.FilesReceived, afterResume.Progress.FilesReceived],
            [.. native.Mutations], staleCode, changedIntent, exactReplay, readPaused, travelAfter == travelBaseline, foreignStatus, revokedDenied, unknownHidden));

        PcMirrorHostShare Configure(Guid id, string name, string destination) =>
            new(id, MirrorNativeApi.Folder, new(nativeBase, name + "/"), destination, new(nativeBase, "send/"), "source");
        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        }
        HttpDevicePlaylistExportHost Gateway(HttpClient client, Uri address) => new(client, address, library, new TokenProvider(library, instance, secret));
        static RequestDispatcher Dispatcher(HttpDevicePlaylistExportHost gateway) =>
            new DesktopSync.MediaMirrorHandlers(gateway).Register(new RequestDispatcher.Builder()).Build();
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class MirrorNativeCredentials(Guid expectedLibrary, Guid studio, Guid travel) :
        IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) => ValueTask.FromResult<string?>(
            libraryId != expectedLibrary ? null : deviceId == studio ? "mirror-studio" : deviceId == travel ? "mirror-travel" : null);
        public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == expectedLibrary && senderDeviceId == "source" ? "mirror-send" : null);
    }

    private sealed class MirrorNativeApi(string music)
    {
        public const string Folder = "full-music";
        private volatile bool studioPaused;
        private volatile int received = 78;
        public bool StudioPaused => studioPaused;
        public bool LoseFirstPauseReply { get; set; }
        public ConcurrentQueue<string> Mutations { get; } = new();

        public WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(listener => listener.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapGet("/{peer}/rest/system/status", (string peer, HttpContext context) => Authorized(peer, context)
                ? Results.Json(new { myID = peer == "send" ? "source" : peer }) : Results.Unauthorized());
            app.MapGet("/{peer}/rest/system/connections", (string peer, HttpContext context) => Authorized(peer, context)
                ? Results.Json(new { connections = new { source = new { connected = true } } }) : Results.Unauthorized());
            app.MapGet("/{peer}/rest/config/folders/{folder}", (string peer, string folder, HttpContext context) => Authorized(peer, context) && folder == Folder
                ? Results.Json(new
                {
                    id = folder, type = peer == "send" ? "sendonly" : "receiveonly",
                    path = peer == "send" ? music : peer == "studio" ? "d:/media/music" : "/mnt/music",
                    paused = peer == "studio" && studioPaused,
                    devices = (peer == "send" ? new[] { "source", "studio", "travel" } : ["source", peer]).Select(id => new { deviceID = id })
                }) : Results.NotFound());
            app.MapMethods("/{peer}/rest/config/folders/{folder}", ["PATCH"], async (string peer, string folder, HttpContext context) =>
            {
                if (peer != "studio" || folder != Folder || !Authorized(peer, context))
                    return Results.NotFound();
                var payload = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
                if (payload.EnumerateObject().Count() != 1 || !payload.TryGetProperty("paused", out var flag) ||
                    flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Results.BadRequest();
                studioPaused = flag.GetBoolean();
                Mutations.Enqueue(studioPaused ? "studio:pause" : "studio:resume");
                if (!studioPaused)
                    received = 84;
                if (studioPaused && LoseFirstPauseReply)
                {
                    LoseFirstPauseReply = false;
                    return Results.StatusCode(503);
                }
                return Results.Ok();
            });
            app.MapGet("/{peer}/rest/db/status", (string peer, string folder, HttpContext context) =>
            {
                if (peer == "send" || !Authorized(peer, context) || folder != Folder || peer == "studio" && studioPaused)
                    return Results.NotFound();
                var count = peer == "studio" ? received : 43;
                return Results.Json(new { inSyncFiles = count, globalFiles = 100, needTotalItems = 100 - count, pullErrors = 0, state = "syncing" });
            });
            app.MapPost("/{peer}/rest/db/scan", (string peer, string folder, HttpContext context) =>
            {
                if (peer != "studio" || !Authorized(peer, context) || folder != Folder || studioPaused)
                    return Results.NotFound();
                Mutations.Enqueue("studio:rescan");
                return Results.Ok();
            });
            return app;
        }

        private static bool Authorized(string peer, HttpContext context) => (peer is "send" or "studio" or "travel") &&
            context.Request.Headers.Authorization == $"Bearer mirror-{peer}";
    }
}
