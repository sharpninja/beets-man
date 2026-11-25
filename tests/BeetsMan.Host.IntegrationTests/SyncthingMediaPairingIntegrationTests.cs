using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Binds pairing assertions to the production Syncthing adapter and local sender roots.</summary>
/// <remarks>2026-11-24: Verify STB10 sender offers for FR-FILESYNC-001#ac-mirror, ac-layouts and ac-safety.</remarks>
public sealed class SyncthingMediaPairingIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "beets-pairing-" + Guid.NewGuid().ToString("N"));

    /// <summary>Proves the Android offer, PC membership append, and rejection of unrelated native scopes.</summary>
    [Fact]
    public async Task PairingOffersOnlyTheApprovedSenderFolderAndLeavesReceiverConsentAlone()
    {
        var token = TestContext.Current.CancellationToken;
        var library = Guid.NewGuid();
        var android = Guid.NewGuid();
        var desktop = Guid.NewGuid();
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "android-export")).FullName;
        var sourceTrack = Path.Combine(music, "Source.flac");
        await File.WriteAllTextAsync(sourceTrack, "original", token);
        var native = new PairingNativeFixture(music);
        using var transport = new HttpClient(native);
        var credentials = new PairingCredentials(library, android, desktop);
        var pairing = new SyncthingMediaShareOffer(transport, credentials, credentials);
        var phone = new RegisteredMediaPairing(library, android, "Phone", MediaPeerType.Android, "android-export",
            new("https://source.test/send/"), "host", export, new("https://phone.test/phone/"), "phone", "/storage/emulated/0/Music/Beets");
        var pc = new RegisteredMediaPairing(library, desktop, "Studio", MediaPeerType.PcMirror, "pc-library",
            new("https://source.test/send/"), "host", music, new("https://studio.test/studio/"), "studio", "D:/Library/Music");

        var offeredAndroid = Value(await pairing.OfferAsync(phone, token));
        var offeredMirror = Value(await pairing.OfferAsync(pc, token));
        var afterFirstOffer = native.SenderWrites.Count;
        var again = Value(await pairing.OfferAsync(phone, token));
        var mirrorAgain = Value(await pairing.OfferAsync(pc, token));
        var verifiedReplay = native.SenderWrites.Count == afterFirstOffer && again == offeredAndroid && mirrorAgain == offeredMirror;
        native.PhoneIdentity = "unexpected-phone";
        var wrongReceiver = Assert.IsType<HostOutcome<MediaShareOffer>.Rejected>(await pairing.OfferAsync(phone, token)).Code;
        native.PhoneIdentity = "phone";
        native.OverrideMirrorRoot = export;
        var wrongFolder = Assert.IsType<HostOutcome<MediaShareOffer>.Rejected>(await pairing.OfferAsync(pc, token)).Code;

        StageAssertions.VerifyMediaPairing(new([.. native.SenderWrites], native.ReceiverWrites == 0, !offeredAndroid.ReceiverAccepted,
            offeredMirror.ReceiverAccepted, native.OtherMemberKept, verifiedReplay, wrongReceiver, wrongFolder,
            await File.ReadAllTextAsync(sourceTrack, token) == "original" && Directory.EnumerateFiles(export).Count() == 0));
    }

    private static T Value<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class PairingCredentials(Guid library, Guid android, Guid desktop) :
        IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == library && deviceId == android ? "fixture-phone" :
                libraryId == library && deviceId == desktop ? "fixture-studio" : null);

        public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == library && senderDeviceId == "host" ? "fixture-send" : null);
    }

    private sealed class PairingNativeFixture(string music) : HttpMessageHandler
    {
        private static readonly JsonElement OtherMember = JsonSerializer.SerializeToElement(new { deviceID = "other-pc", introducedBy = "kept", marker = 42 });
        private JsonElement[] mirrorMembers = [JsonSerializer.SerializeToElement(new { deviceID = "host" }), OtherMember];
        private JsonElement? androidFolder;
        private bool phoneEnrolled;
        public string PhoneIdentity { get; set; } = "phone";
        public string? OverrideMirrorRoot { get; set; }
        public int ReceiverWrites { get; private set; }
        public List<string> SenderWrites { get; } = [];
        public bool OtherMemberKept => mirrorMembers.Any(member => JsonElement.DeepEquals(member, OtherMember)) &&
            mirrorMembers.Select(member => member.GetProperty("deviceID").GetString()).SequenceEqual(["host", "other-pc", "studio"]);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var peer = parts[0];
            var route = string.Join('/', parts.Skip(1));
            Assert.Equal($"fixture-{peer}", request.Headers.Authorization?.Parameter);
            var response = request.Method == HttpMethod.Get ? Read() : await WriteAsync();
            response.RequestMessage = request;
            return response;

            HttpResponseMessage Read() => (peer, route) switch
            {
                ("send", "rest/system/status") => Json(new { myID = "host" }),
                ("phone", "rest/system/status") => Json(new { myID = PhoneIdentity }),
                ("studio", "rest/system/status") => Json(new { myID = "studio" }),
                ("phone", "rest/config/folders/android-export") => new(HttpStatusCode.NotFound),
                ("studio", "rest/config/folders/pc-library") => Json(new
                {
                    id = "pc-library", type = "receiveonly", path = @"d:\library\music", devices = new[] { new { deviceID = "host" }, new { deviceID = "studio" } }
                }),
                ("send", "rest/config/folders/android-export") => androidFolder is { } folder ? Json(folder) : new(HttpStatusCode.NotFound),
                ("send", "rest/config/folders/pc-library") => Json(new
                {
                    id = "pc-library", type = "sendonly", path = OverrideMirrorRoot ?? music, devices = mirrorMembers, rescanIntervalS = 125
                }),
                ("send", "rest/config/devices/phone") => phoneEnrolled ? Json(new { deviceID = "phone" }) : new(HttpStatusCode.NotFound),
                ("send", "rest/config/devices/studio") => Json(new { deviceID = "studio" }),
                _ => throw new InvalidOperationException($"Unexpected fixture route for {peer} {route}.")
            };

            async Task<HttpResponseMessage> WriteAsync()
            {
                if (peer != "send")
                {
                    ReceiverWrites++;
                    return new(HttpStatusCode.Forbidden);
                }
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (request.Method == HttpMethod.Post && route == "rest/config/devices")
                {
                    Assert.False(phoneEnrolled);
                    Assert.Equal("phone", payload.GetProperty("deviceID").GetString());
                    Assert.False(payload.GetProperty("introducer").GetBoolean());
                    Assert.False(payload.GetProperty("autoAcceptFolders").GetBoolean());
                    phoneEnrolled = true;
                    SenderWrites.Add("device:phone");
                }
                else if (request.Method == HttpMethod.Post && route == "rest/config/folders")
                {
                    Assert.Null(androidFolder);
                    Assert.Equal("android-export", payload.GetProperty("id").GetString());
                    Assert.Equal("sendonly", payload.GetProperty("type").GetString());
                    Assert.NotEqual(music, payload.GetProperty("path").GetString());
                    androidFolder = payload;
                    SenderWrites.Add("new:android-export");
                }
                else
                {
                    Assert.Equal(HttpMethod.Patch, request.Method);
                    Assert.Equal("rest/config/folders/pc-library", route);
                    var property = Assert.Single(payload.EnumerateObject().ToArray());
                    Assert.Equal("devices", property.Name);
                    mirrorMembers = property.Value.EnumerateArray().ToArray();
                    SenderWrites.Add("members:pc-library");
                }
                return new(HttpStatusCode.OK);
            }
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
