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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using DesktopSync = BeetsMan.Desktop.Application.MediaSync;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Executes scoped approvals through HTTPS and the Toolkit command and recovers the original response.</summary>
    /// <remarks>2026-11-24: Bind STB10 pairing outcomes for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
    [Fact]
    public async Task PairMediaPeerPersistsTheLayoutAndReplaysAfterHostRestart()
    {
        var database = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var phone = Guid.NewGuid();
        var travel = Guid.NewGuid();
        var pc = Guid.NewGuid();
        var nativeBase = new Uri("http://127.0.0.1:1/");
        var legacy = new RegisteredAndroidExport(library, Guid.NewGuid(), 9, "legacy-export", "legacy", new(nativeBase, "legacy/"), false);
        SeedHost(database, [legacy]);
        using (var db = new LiteDatabase(database))
        {
            Register(phone, "Android", "phone");
            Register(travel, "Android", "travel");
            Register(pc, "Desktop", "studio");

            void Register(Guid id, string role, string nativeId) => db.GetCollection<BsonDocument>("registered_instances")
                .Insert(new BsonDocument { ["_id"] = id, ["LibraryId"] = library, ["Role"] = role, ["Status"] = "Active",
                    ["Name"] = nativeId, ["SyncthingDeviceId"] = nativeId });
        }

        const string androidRoot = "/storage/emulated/0/Music/Beets";
        const string pcRoot = "D:/Media/Music";
        var phoneRoute = ConfigureAndroid(phone, "phone-export", "phone");
        var travelRoute = ConfigureAndroid(travel, "travel-export", "travel");
        var options = new LibraryHostOptions(library, database, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [phoneRoute, travelRoute], PcMirrors: [new(pc, "pc-library", new(nativeBase, "studio/"), pcRoot, new(nativeBase, "send/"), "source")]);
        var offers = new RecordingShareOffer();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var identity = new Verifier(new(library, instance), secret, null);
        using var certificate = CreateCertificate();
        var operation = Guid.NewGuid();
        var proposal = new MediaPairingRequest(instance, operation, phone, MediaPeerType.Android, androidRoot, "poweramp", 0);
        string original, recovered, pcPhase;
        MediaPairingResponse paired;
        int missing, wrongLibrary, wrongOrigin, wrongRoot, wrongRole, wrongProfile, stale, changedIntent;
        bool dropped, otherPreserved;

        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var wire = new LostResponseHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(wire) { BaseAddress = address };
            missing = (await Pair(client, proposal, authenticated: false)).Status;
            wrongLibrary = (await Pair(client, proposal, scope: Guid.NewGuid())).Status;
            wrongOrigin = (await Pair(client, proposal with { OriginInstanceId = Guid.NewGuid() })).Status;
            wrongRoot = (await Pair(client, proposal with { DestinationRoot = androidRoot + "/elsewhere" })).Status;
            wrongRole = (await Pair(client, proposal with { PeerId = pc })).Status;
            wrongProfile = (await Pair(client, proposal with { ProfileId = "unconfigured" })).Status;
            Assert.Empty(offers.Targets);

            var gateway = Gateway(client, address);
            var dispatch = new DesktopSync.MediaMirrorHandlers(gateway).Register(new RequestDispatcher.Builder()).Build();
            var context = new CommandContext(new(Guid.NewGuid(), instance), operation);
            var command = new DesktopSync.PairMediaPeer(phone, new(DesktopSync.MediaPeerKind.Android, androidRoot, "poweramp"), 0);
            wire.DropNextSuccess = true;
            dropped = await dispatch.SendAsync(command, context, Token) is Outcome<DesktopSync.MediaSyncState>.DependencyFailure
                { Code: "media-pairing-host-unreachable", Retryable: true };
            var response = await Pair(client, proposal);
            Assert.Equal(200, response.Status);
            original = response.Body;
            paired = Deserialize(original);
            Assert.Equal(operation, paired.OperationId);
            Assert.Equal(phone, paired.PeerId);
            Assert.Equal(phoneRoute.ReceiverEndpoint, Assert.Single(offers.Targets).ReceiverEndpoint);
            Assert.Equal(phoneRoute.Sender.ExportRoot, Assert.Single(offers.Targets).SenderRoot);
            changedIntent = (await Pair(client, proposal with { ProfileId = "musicolet" })).Status;
            stale = (await Pair(client, proposal with { OperationId = Guid.NewGuid(), PeerId = travel, ProfileId = null })).Status;

            var window = Guid.NewGuid();
            var services = new PresentationServices(dispatch, new InlineScheduler(), instance);
            using var shell = new ShellViewModel(services, window);
            using var viewModel = new MediaSyncViewModel(services, Guid.NewGuid(), shell);
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.ShareFilesCommand.ExecuteAsync(new PairPeerInput(travel, new(DesktopSync.MediaPeerKind.Android, androidRoot, null), paired.State.Revision));
            Assert.Null(viewModel.Failure);
            Assert.Equal(travel, viewModel.SelectedPeerId);
            var pcResponse = await Pair(client, new(instance, Guid.NewGuid(), pc, MediaPeerType.PcMirror, pcRoot, null, viewModel.State!.Revision));
            Assert.Equal(200, pcResponse.Status);
            var pcState = Deserialize(pcResponse.Body).State;
            pcPhase = pcState.Shares.Single(share => share.PeerId == pc).Phase.ToString();
            otherPreserved = pcState.Shares.Single(share => share.PeerId == legacy.DeviceId) == paired.State.Shares.Single(share => share.PeerId == legacy.DeviceId);
        }

        string[] layouts;
        string implicitProfile;
        var offerCount = offers.Targets.Count;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            var replay = await Pair(client, proposal);
            Assert.Equal(200, replay.Status);
            recovered = replay.Body;

            var gateway = Gateway(client, address);
            var context = new RequestContext(Guid.NewGuid(), instance);
            var first = Assert.IsType<Outcome<DesktopSync.DeviceLayoutsSnapshot>.Success>(
                await gateway.ReadLayoutsAsync(new(phone), context, Token)).Value.ActiveProfile;
            var second = Assert.IsType<Outcome<DesktopSync.DeviceLayoutsSnapshot>.Success>(
                await gateway.ReadLayoutsAsync(new(travel), context, Token)).Value.ActiveProfile;
            layouts = [$"{first!.ProfileId}@{first.Revision}", $"{second!.ProfileId}@{second.Revision}"];
            implicitProfile = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(
                await gateway.ReadPlanAsync(new(phone), context, Token)).Value.ProfileId;
        }

        StageAssertions.VerifyPairingCommand(new(missing, wrongLibrary, wrongOrigin, wrongRoot, wrongRole, wrongProfile, stale, changedIntent,
            [.. offers.Targets.Select(target => $"{target.Kind}:{target.FolderId}")],
            paired.State.Shares.Single(share => share.PeerId == phone).Phase.ToString(), pcPhase, layouts, implicitProfile,
            dropped && recovered == original, offers.Targets.Count == offerCount, otherPreserved));
        Assert.Equal(music, offers.Targets.Last().SenderRoot);
        using var persisted = new LiteDatabase(database);
        var registrations = persisted.GetCollection<BsonDocument>("registered_instances");
        var legacyRow = registrations.FindById(legacy.DeviceId)["AndroidExport"].AsDocument;
        Assert.Equal(9, legacyRow["Revision"].AsInt64);
        Assert.False(legacyRow.ContainsKey("InitialProfileId"));
        var approvedPhone = registrations.FindById(phone);
        Assert.Equal(androidRoot, approvedPhone["AndroidExport"].AsDocument["ReceiverRoot"].AsString);
        Assert.False(approvedPhone.ContainsKey("PendingMediaPairing"));

        AndroidHostExport ConfigureAndroid(Guid peer, string folder, string recipient) => new(peer, folder,
            new(new(nativeBase, "send/"), "source", Directory.CreateDirectory(Path.Combine(root, folder)).FullName), new(nativeBase, recipient + "/"));

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IMediaShareOffer>(offers);
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        }

        HttpDevicePlaylistExportHost Gateway(HttpClient client, Uri address) => new(client, address, library, new TokenProvider(library, instance, secret));

        async Task<(int Status, string Body)> Pair(HttpClient client, MediaPairingRequest body, bool authenticated = true, Guid? scope = null)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, MediaMirrorRoutes.PairPath(scope ?? library)) { Content = JsonContent.Create(body) };
            if (authenticated)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await client.SendAsync(message, Token);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Token));
        }

        static MediaPairingResponse Deserialize(string json) => JsonSerializer.Deserialize<MediaPairingResponse>(json, JsonSerializerOptions.Web)!;
    }

    /// <summary>Records which exact native pairing offers reach the approved dependency boundary.</summary>
    private sealed class RecordingShareOffer : IMediaShareOffer
    {
        public ConcurrentQueue<RegisteredMediaPairing> Targets { get; } = new();

        /// <inheritdoc />
        public Task<HostOutcome<MediaShareOffer>> OfferAsync(RegisteredMediaPairing target, CancellationToken token)
        {
            Targets.Enqueue(target);
            return Task.FromResult<HostOutcome<MediaShareOffer>>(new HostOutcome<MediaShareOffer>.Success(new(false)));
        }
    }
}
