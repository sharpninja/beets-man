using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks actual receiver inventory interpretation against independently scripted HTTP responses.</summary>
/// <remarks>2026-11-24: Bind the TEST-FILESYNC-001 inventory assertions to the REST adapter for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class SyncthingReceiverInventoryTests
{
    private const string Device = "fixture-receiver";
    private const string Folder = "android-share";
    private const string Present = "Music/Already there.flac";
    private const string Missing = "Music/Song #2.flac";

    [Fact]
    public async Task UsesTheSelectedReceiverLocalIndexAndRejectsUnprovenAbsence()
    {
        var correct = Assert.IsType<HostOutcome<ImmutableDictionary<string, bool>>.Success>(await ReadAsync(Scenario.Normal)).Value;
        StageAssertions.VerifyReceiverInventory(new(
            [.. correct.Where(item => item.Value).Select(item => item.Key)], [.. correct.Where(item => !item.Value).Select(item => item.Key)],
            Failure(await ReadAsync(Scenario.WrongPeer)), Failure(await ReadAsync(Scenario.ChangedIndex)), Failure(await ReadAsync(Scenario.Directory)),
            Failure(await ReadAsync(Scenario.IgnorePatterns)), Failure(await ReadAsync(Scenario.UnknownFolder))));
    }

    [Theory]
    [InlineData(Scenario.GlobalOnly, false)]
    [InlineData(Scenario.LocalOnly, true)]
    [InlineData(Scenario.Deleted, false)]
    public async Task UsesLocalFileStatusEvenWhenTheGlobalViewDiffers(Scenario scenario, bool expected)
    {
        var outcome = Assert.IsType<HostOutcome<ImmutableDictionary<string, bool>>.Success>(await ReadAsync(scenario));
        Assert.Equal(expected, outcome.Value[Missing]);
    }

    [Fact]
    public async Task RejectsAClientRedirectEvenIfItMutatesTheOriginalRequestUri()
    {
        Assert.Equal("export-inventory-unavailable", Failure(await ReadAsync(Scenario.Redirected)));
    }

    public enum Scenario { Normal, WrongPeer, ChangedIndex, Directory, IgnorePatterns, UnknownFolder, GlobalOnly, LocalOnly, Deleted, Redirected }

    private static string Failure(HostOutcome<ImmutableDictionary<string, bool>> outcome) => outcome switch
    {
        HostOutcome<ImmutableDictionary<string, bool>>.Unavailable unavailable => unavailable.Code,
        HostOutcome<ImmutableDictionary<string, bool>>.Rejected rejected => rejected.Code,
        _ => throw new ArgumentException("An expected inventory failure succeeded.", nameof(outcome))
    };

    private static async Task<HostOutcome<ImmutableDictionary<string, bool>>> ReadAsync(Scenario scenario)
    {
        using var fixture = new ReceiverApi(scenario);
        using var client = new HttpClient(fixture);
        var receiver = new SyncthingReceiver(new("https://receiver.test/service"), Device, Folder);
        var result = await new SyncthingReceiverInventory(client).ReadAsync(receiver, [Present, Missing, Missing],
            "fixture-token", TestContext.Current.CancellationToken);
        Assert.InRange(fixture.FileRequests, 0, 2);
        if (scenario is Scenario.Normal or Scenario.ChangedIndex)
            Assert.Equal(2, fixture.FileRequests);
        if (scenario is Scenario.WrongPeer or Scenario.Redirected)
            Assert.Equal(0, fixture.FileRequests);
        return result;
    }

    private sealed class ReceiverApi(Scenario scenario) : HttpMessageHandler
    {
        private int statusRequests;
        public int FileRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("receiver.test", request.RequestUri!.Host);
            var response = request.RequestUri.PathAndQuery switch
            {
                "/service/rest/system/status" => Json(new { myID = scenario == Scenario.WrongPeer ? "another-peer" : Device }),
                "/service/rest/db/status?folder=android-share" => ReadStatus(),
                "/service/rest/db/file?folder=android-share&file=Music%2FAlready%20there.flac" => ReadFile(Present),
                "/service/rest/db/file?folder=android-share&file=Music%2FSong%20%232.flac" => ReadFile(Missing),
                _ => throw new InvalidOperationException("The adapter attempted an unscoped receiver API call.")
            };
            if (scenario == Scenario.Redirected)
                request.RequestUri = new Uri("https://another.test/service/rest/system/status");
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage ReadStatus()
        {
            statusRequests++;
            return Json(new { state = "idle", sequence = scenario == Scenario.ChangedIndex && statusRequests == 2 ? 31 : 30,
                stateChanged = "2026-11-23T18:00:00Z", ignorePatterns = scenario == Scenario.IgnorePatterns });
        }

        private HttpResponseMessage ReadFile(string path)
        {
            FileRequests++;
            if (path == Present)
                return Json(new { local = Entry(path), global = Entry(path, deleted: true) });
            return scenario switch
            {
                Scenario.Directory => Json(new { local = Entry(path, type: "FILE_INFO_TYPE_DIRECTORY") }),
                Scenario.GlobalOnly => Json(new { local = Entry(string.Empty), global = Entry(path) }),
                Scenario.LocalOnly => Json(new { local = Entry(path, invalid: true, localFlags: 8), global = Entry(string.Empty) }),
                Scenario.Deleted => Json(new { local = Entry(path, deleted: true), global = Entry(path) }),
                Scenario.UnknownFolder => MissingResponse("Folder does not exist\n"),
                _ => MissingResponse("No such object in the index\n")
            };
        }

        private static object Entry(string name, string type = "FILE_INFO_TYPE_FILE", bool deleted = false, bool invalid = false, int localFlags = 0) =>
            new { name, type, deleted, invalid, ignored = false, mustRescan = false, localFlags };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

        private static HttpResponseMessage MissingResponse(string message) => new(HttpStatusCode.NotFound) { Content = new StringContent(message) };
    }
}
