using System.Collections.Immutable;
using System.Net;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using DesktopSync = BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies the paired Android folder changes visible status and controls the validity of receiver evidence.</summary>
    /// <remarks>2026-11-24: Bind STB10 and TEST-FILESYNC-002 to the receiver's approved path for FR-FILESYNC-001#ac-layouts, ac-playlist-drop, and ac-safety.</remarks>
    [Fact]
    public async Task AndroidAcceptanceUsesVerifiedFolderBeforeReadingOrTrustingTheDeviceIndex()
    {
        var device = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        var database = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "android")).FullName;
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            Register(device, AndroidNativeReceiver.Device);
            Register(otherDevice, "later-receiver");

            void Register(Guid id, string nativeId) => db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
            {
                ["_id"] = id, ["LibraryId"] = library, ["Role"] = "Android", ["Status"] = "Active",
                ["Name"] = nativeId, ["SyncthingDeviceId"] = nativeId
            });
        }

        var native = new AndroidNativeReceiver();
        await using var nativeApp = native.Build();
        await nativeApp.StartAsync(Token);
        var endpoint = new Uri(nativeApp.Urls.Single() + "/");
        var otherExport = Directory.CreateDirectory(Path.Combine(root, "other-android")).FullName;
        var options = new LibraryHostOptions(library, database, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [new(device, AndroidNativeReceiver.Folder, new(new(endpoint, "sender/"), AndroidNativeReceiver.Sender, export), endpoint),
                new(otherDevice, "other-export", new(new(endpoint, "sender/"), AndroidNativeReceiver.Sender, otherExport), new(endpoint, "other/"))]);
        var credentials = new AndroidAcceptanceCredentials(library, device);
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IMediaShareOffer>(new RecordingShareOffer());
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var host = LibraryHostStartup.Build(builder, options, new Verifier(new(library, instance), secret, null), credentials, credentials);
        await host.StartAsync(Token);
        var hostAddress = new Uri(host.Urls.Single());
        using var client = new HttpClient(CreateClientHandler(certificate));
        var gateway = new HttpDevicePlaylistExportHost(client, hostAddress, library, new TokenProvider(library, instance, secret));
        var dispatch = new DesktopSync.MediaMirrorHandlers(gateway).Register(new RequestDispatcher.Builder()).Build();
        var services = new PresentationServices(dispatch, new InlineScheduler(), instance);
        using var shell = new ShellViewModel(services, Guid.NewGuid());
        using var viewModel = new MediaSyncViewModel(services, Guid.NewGuid(), shell);
        viewModel.Activate(viewModel.Scope.OwnerId);
        await viewModel.ShareFilesCommand.ExecuteAsync(new PairPeerInput(device,
            new(DesktopSync.MediaPeerKind.Android, AndroidNativeReceiver.ApprovedRoot, null), 0));
        Assert.Null(viewModel.Failure);
        Assert.Equal(DesktopSync.MediaSharePhase.AwaitingAcceptance, viewModel.State!.Shares.Single().Progress!.Phase);

        var sources = await host.Services.GetRequiredService<LiteDbHostPlaylistStore>()
            .ReadDevicePlaylistSourcesAsync(library, device, [], Token);
        var registered = Assert.IsType<HostOutcome<DevicePlaylistSources>.Success>(sources).Value.Destination;
        Assert.Equal(AndroidNativeReceiver.Sender, registered.ApprovedSenderId);
        Assert.Equal(AndroidNativeReceiver.ApprovedRoot, registered.ReceiverRoot);
        var presence = host.Services.GetRequiredService<RegisteredDeviceExportPresence>();
        var verifier = host.Services.GetRequiredService<IDeviceExportReceiverVerifier>();
        string[] paths = [AndroidNativeReceiver.Track];

        var missing = await Status(AndroidNativeReceiver.State.Absent);
        var awaitingRevision = viewModel.State!.Revision;
        var beforeInventory = native.IndexReads;
        var waiting = await presence.ReadAsync(registered, paths, Token);
        Assert.Equal("export-receiver-awaiting-acceptance", Assert.IsType<HostOutcome<ImmutableDictionary<string, bool>>.Unavailable>(waiting).Code);
        var absentInventory = native.IndexReads - beforeInventory;
        var accepted = await Status(AndroidNativeReceiver.State.Ready);
        var readyRevision = viewModel.State!.Revision;
        await Status(AndroidNativeReceiver.State.Ready);
        var repeatedRevision = viewModel.State!.Revision;
        await viewModel.ShareFilesCommand.ExecuteAsync(new PairPeerInput(otherDevice,
            new(DesktopSync.MediaPeerKind.Android, AndroidNativeReceiver.ApprovedRoot, null), repeatedRevision));
        Assert.Null(viewModel.Failure);
        StageAssertions.VerifyAndroidAcceptanceContinuity(new(awaitingRevision, readyRevision, repeatedRevision,
            viewModel.State!.Shares.Single(share => share.PeerId == device).Progress?.Phase == DesktopSync.MediaSharePhase.Ready));
        var initialPresence = await presence.ReadAsync(registered, paths, Token);
        var validTransfer = await verifier.ObserveReceiverAsync(registered, [new(AndroidNativeReceiver.Track, "digest", 7, "source:1")], Token);
        var paused = await Status(AndroidNativeReceiver.State.Paused);
        var rootMismatch = await Status(AndroidNativeReceiver.State.WrongRoot);
        var rootFailure = viewModel.State!.Shares.Single(share => share.PeerId == device).Progress!.FailureCode;
        var offline = await Status(AndroidNativeReceiver.State.Offline);

        native.Condition = AndroidNativeReceiver.State.WrongMember;
        beforeInventory = native.IndexReads;
        var foreignMembership = await presence.ReadAsync(registered, paths, Token);
        var wrongScopeInventory = native.IndexReads - beforeInventory;
        var membershipFailure = Assert.IsType<HostOutcome<ImmutableDictionary<string, bool>>.Rejected>(foreignMembership).Code;
        native.Condition = AndroidNativeReceiver.State.RemapAfterIndex;
        var beforeConfig = native.FolderReads;
        var changedScope = await verifier.ObserveReceiverAsync(registered, [new(AndroidNativeReceiver.Track, "digest", 7, "source:1")], Token);
        Assert.Equal(rootFailure, Assert.IsType<HostOutcome<DeviceExportReceiverProgress>.Rejected>(changedScope).Code);
        var changedScopeReads = native.FolderReads - beforeConfig;

        native.Condition = AndroidNativeReceiver.State.Absent;
        beforeConfig = native.FolderReads;
        var legacyPresence = await presence.ReadAsync(registered with { ReceiverRoot = null, ApprovedSenderId = null }, paths, Token);
        StageAssertions.VerifyAndroidAcceptance(new([missing, accepted, paused, rootMismatch, offline], [rootFailure!, membershipFailure],
            native.UnexpectedMethods == 0, absentInventory, wrongScopeInventory, changedScopeReads,
            initialPresence is HostOutcome<ImmutableDictionary<string, bool>>.Success { Value: var initial } && initial[paths[0]],
            validTransfer is HostOutcome<DeviceExportReceiverProgress>.Success { Value: { Completed: true, BytesVerified: 7 } },
            legacyPresence is HostOutcome<ImmutableDictionary<string, bool>>.Success { Value: var legacy } && legacy[paths[0]] &&
                native.FolderReads == beforeConfig));

        async Task<string> Status(AndroidNativeReceiver.State condition)
        {
            native.Condition = condition;
            await viewModel.OpenMirrorsCommand.ExecuteAsync(null);
            Assert.Null(viewModel.Failure);
            var share = viewModel.State!.Shares.Single(share => share.PeerId == device);
            Assert.Equal(condition == AndroidNativeReceiver.State.Paused, share.Paused);
            return (share.Progress?.Phase ?? DesktopSync.MediaSharePhase.Unknown).ToString();
        }
    }

    private sealed class AndroidAcceptanceCredentials(Guid library, Guid device) : IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == library && deviceId == device ? "fixture-android" : null);

        public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) => ValueTask.FromResult<string?>(null);
    }

    /// <summary>Scripts receiver configuration and a verified-looking file under the same folder ID.</summary>
    private sealed class AndroidNativeReceiver
    {
        public const string Device = "receiver";
        public const string Sender = "source";
        public const string Folder = "phone-accepted";
        public const string Track = "Music/Song.flac";
        public const string ApprovedRoot = "/storage/emulated/0/Music/Beets";
        private int folderReads, indexReads, unexpectedMethods;
        private volatile State condition;
        private volatile bool remapped;
        public int FolderReads => Volatile.Read(ref folderReads);
        public int IndexReads => Volatile.Read(ref indexReads);
        public int UnexpectedMethods => Volatile.Read(ref unexpectedMethods);
        public State Condition { get => condition; set { remapped = false; condition = value; } }
        public enum State { Absent, Ready, Paused, WrongRoot, WrongMember, Offline, RemapAfterIndex }

        public WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (!HttpMethods.IsGet(context.Request.Method))
                {
                    Interlocked.Increment(ref unexpectedMethods);
                    context.Response.StatusCode = 405;
                }
                else if (context.Request.Headers.Authorization != "Bearer fixture-android")
                    context.Response.StatusCode = 401;
                else
                    await next(context);
            });
            app.MapGet("/rest/system/status", () => Condition == State.Offline ? Results.StatusCode(503) : Results.Json(new { myID = Device }));
            app.MapGet("/rest/config/folders/{id}", (string id) =>
            {
                Interlocked.Increment(ref folderReads);
                if (id != Folder || Condition == State.Absent)
                    return Results.NotFound();
                return Results.Json(new
                {
                    id, type = "receiveonly", path = Condition == State.WrongRoot || remapped ? ApprovedRoot + "/elsewhere" : ApprovedRoot,
                    paused = Condition == State.Paused,
                    devices = new[] { new { deviceID = Condition == State.WrongMember ? "other-sender" : Sender }, new { deviceID = Device } }
                });
            });
            app.MapGet("/rest/db/status", (string folder) =>
            {
                Interlocked.Increment(ref indexReads);
                return folder != Folder ? Results.BadRequest() : Results.Json(new
                { state = "idle", sequence = 41, stateChanged = "2026-11-24T17:46:00Z", ignorePatterns = false });
            });
            app.MapGet("/rest/db/file", (string folder, string file) =>
            {
                Interlocked.Increment(ref indexReads);
                if (Condition == State.RemapAfterIndex)
                    remapped = true;
                return folder != Folder || file != Track ? Results.BadRequest() : Results.Json(new
                {
                    local = new { name = file, type = "FILE_INFO_TYPE_FILE", size = 7, version = new[] { "source:1" },
                        deleted = false, invalid = false, ignored = false, mustRescan = false }
                });
            });
            return app;
        }
    }
}
