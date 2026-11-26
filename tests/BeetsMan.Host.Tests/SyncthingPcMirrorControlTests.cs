using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks the production PC mirror control against an independent Syncthing REST fixture.</summary>
/// <remarks>2026-11-24: Bind the mock-proven STB10 mirror tests for FR-FILESYNC-001#ac-mirror.</remarks>
public sealed partial class FILESYNCAcceptanceTests
{
    /// <summary>Verifies full-library scope, pause progress, resume and safe controls for multiple PCs.</summary>
    /// <remarks>2026-11-26: Run the named TEST-FILESYNC-001 mirror case for FR-FILESYNC-001#ac-mirror.</remarks>
    [Fact]
    public async Task Mirror_MatchesContract()
    {
        var token = TestContext.Current.CancellationToken;
        var library = Guid.NewGuid();
        var studioId = Guid.NewGuid();
        var travelId = Guid.NewGuid();
        var music = Path.Combine(Path.GetTempPath(), "beets-music-fixture");
        var studio = new RegisteredPcMirror(library, studioId, "music-full", new("https://studio.test/studio/"), "studio",
            "D:/Mirrors/Music", new("https://host.test/send/"), "host", music);
        var travel = studio with
        {
            PeerId = travelId, ReceiverEndpoint = new("https://travel.test/travel/"), ReceiverDeviceId = "travel", ReceiverRoot = "/local/mirrors/music"
        };
        var native = new NativeFixture(music);
        using var client = new HttpClient(native);
        var credentials = new MirrorCredentials(library, studioId, travelId);
        var control = new SyncthingPcMirrorControl(client, credentials, credentials);
        var original = Value(await control.ApplyAsync(studio, PcMirrorAction.Observe, null, token));
        var otherBefore = Value(await control.ApplyAsync(travel, PcMirrorAction.Observe, null, token));
        var paused = Value(await control.ApplyAsync(studio, PcMirrorAction.Pause, original, token));
        var repeated = Value(await control.ApplyAsync(studio, PcMirrorAction.Pause, paused, token));
        var resumed = Value(await control.ApplyAsync(studio, PcMirrorAction.Resume, paused, token));
        var scanned = Value(await control.ApplyAsync(studio, PcMirrorAction.Rescan, resumed, token));
        var otherAfter = Value(await control.ApplyAsync(travel, PcMirrorAction.Observe, otherBefore, token));
        native.StudioIdentity = "spoof";
        var wrongIdentity = Assert.IsType<HostOutcome<PcMirrorNativeState>.Rejected>(
            await control.ApplyAsync(studio, PcMirrorAction.Pause, resumed, token));
        native.StudioIdentity = "studio";
        native.UnexpectedParticipant = true;
        var wrongScope = Assert.IsType<HostOutcome<PcMirrorNativeState>.Rejected>(
            await control.ApplyAsync(studio, PcMirrorAction.Rescan, resumed, token));
        native.UnexpectedParticipant = false;
        native.TravelAccepts = false;
        var pending = Value(await control.ApplyAsync(travel, PcMirrorAction.Observe, otherAfter, token));
        Assert.Equal("mirror-share-not-accepted", Assert.IsType<HostOutcome<PcMirrorNativeState>.Rejected>(
            await control.ApplyAsync(travel, PcMirrorAction.Pause, pending, token)).Code);

        StageAssertions.VerifyPcMirror(new([original.Phase.ToString(), paused.Phase.ToString(), resumed.Phase.ToString()],
            [original.FilesReceived, paused.FilesReceived, resumed.FilesReceived, resumed.TotalFiles], original.Connected && resumed.Connected,
            repeated == paused && native.StudioPausedStatusReads == 0, [.. native.Mutations],
            otherBefore == otherAfter && otherAfter.FilesReceived == 43 && scanned == resumed,
            wrongIdentity.Code, wrongScope.Code, pending.Phase.ToString()));
    }

    private sealed class MirrorCredentials(Guid library, Guid studio, Guid travel) : IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId != library ? null : deviceId == studio ? "fixture-studio" : deviceId == travel ? "fixture-travel" : null);

        public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == library && senderDeviceId == "host" ? "fixture-send" : null);
    }

    private sealed class NativeFixture(string music) : HttpMessageHandler
    {
        private bool paused;
        private int studioCount = 78;
        public bool TravelAccepts { get; set; } = true;
        public bool UnexpectedParticipant { get; set; }
        public string StudioIdentity { get; set; } = "studio";
        public int StudioPausedStatusReads { get; private set; }
        public List<string> Mutations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var peer = path[0];
            Assert.Equal($"fixture-{peer}", request.Headers.Authorization?.Parameter);
            Assert.Contains(peer, new[] { "send", "studio", "travel" });
            var route = string.Join('/', path.Skip(1));
            var response = request.Method == HttpMethod.Get ? Read() : await WriteAsync();
            response.RequestMessage = request;
            return response;

            HttpResponseMessage Read() => route switch
            {
                "rest/system/status" => Json(new { myID = peer == "send" ? "host" : peer == "studio" ? StudioIdentity : peer }),
                "rest/system/connections" => Json(new { connections = new Dictionary<string, object> { ["host"] = new { connected = true } } }),
                "rest/config/folders/music-full" => Folder(),
                "rest/db/status" => Status(),
                _ => throw new InvalidOperationException($"Unexpected read for {peer} {route}")
            };

            HttpResponseMessage Folder()
            {
                if (peer == "travel" && !TravelAccepts)
                    return new(HttpStatusCode.NotFound);
                var ids = peer == "send" ? new[] { "host", "studio", "travel" } :
                    UnexpectedParticipant ? ["host", peer, "unexpected"] : new[] { "host", peer };
                return Json(new
                {
                    id = "music-full", type = peer == "send" ? "sendonly" : "receiveonly",
                    path = peer == "send" ? music : peer == "studio" ? @"d:\mirrors\music\" : "/local/mirrors/music/",
                    paused = peer == "studio" && paused,
                    devices = ids.Select(id => new { deviceID = id })
                });
            }

            HttpResponseMessage Status()
            {
                Assert.Equal("?folder=music-full", uri.Query);
                if (peer == "studio" && paused)
                {
                    StudioPausedStatusReads++;
                    return new(HttpStatusCode.NotFound);
                }
                var received = peer == "studio" ? studioCount : 43;
                return Json(new { globalFiles = 100, inSyncFiles = received, needTotalItems = 100 - received, pullErrors = 0, state = "syncing" });
            }

            async Task<HttpResponseMessage> WriteAsync()
            {
                Assert.Equal("studio", peer);
                if (request.Method == HttpMethod.Patch)
                {
                    Assert.Equal("rest/config/folders/music-full", route);
                    var edit = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    var field = Assert.Single(edit.EnumerateObject().ToArray());
                    Assert.Equal("paused", field.Name);
                    paused = field.Value.GetBoolean();
                    if (!paused)
                        studioCount = 84;
                    Mutations.Add(paused ? "studio:pause" : "studio:resume");
                }
                else
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("rest/db/scan", route);
                    Assert.Equal("?folder=music-full", uri.Query);
                    Mutations.Add("studio:rescan");
                }
                return new(HttpStatusCode.OK);
            }
        }

        private static HttpResponseMessage Json<T>(T payload) => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}
