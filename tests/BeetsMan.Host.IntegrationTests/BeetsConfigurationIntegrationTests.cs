using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Host.Adapters.Beets;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using YamlDotNet.RepresentationModel;
using static BeetsMan.Host.Tests.BeetsConfigurationAssertions;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies an authorized host configuration change and a native import using the applied path.</summary>
    /// <remarks>2026-11-25: Bind the mocked settings cases through HTTPS, LiteDB, restart and native beets for TEST-BEETS-002.</remarks>
    [Fact]
    public async Task SavedBeetsConfigurationActivatesAtTheNextNativeCommand()
    {
        var executable = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS") ?? (OperatingSystem.IsLinux() ? "/usr/bin/beet" : null);
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG") ?? (OperatingSystem.IsLinux() ? "/usr/bin/ffmpeg" : null);
        Assert.True(executable is not null && File.Exists(executable), "Set BEETSMAN_TEST_BEETS to the installed beets executable.");
        Assert.True(ffmpeg is not null && File.Exists(ffmpeg), "Set BEETSMAN_TEST_FFMPEG to the installed FFmpeg executable.");
        var database = Path.Combine(root, "beets-settings-projection.db");
        var mi = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var catalog = Path.Combine(root, "native-catalog.db");
        var yaml = Path.Combine(root, "operator-beets.yaml");
        SeedHost(database);
        const string listenerCredential = "fixture-settings-limited";
        var listener = Guid.NewGuid();
        using (var db = new LiteDatabase(database))
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = listener, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        await PrepareNativeFixtureAsync(executable!, yaml, catalog, mi);
        var original = Hash(yaml);
        var options = new LibraryHostOptions(library, database, mi, Path.Combine(root, "uploads"), Path.Combine(root, "work"), [],
            Beets: new(executable!, catalog, OriginalNaming, ConfigurationPath: yaml, AllowedPluginIds: ["info"]));
        var wireSettings = new HostBeetsSettings(catalog, mi, EditedNaming, ImmutableDictionary<string, string>.Empty.Add("behavior", "keep-tags"),
            ["info"], yaml);
        var draft = new SaveHostBeetsConfigurationRequest(instance, Guid.NewGuid(), 0, wireSettings);
        using var certificate = CreateCertificate();
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        {
            [secret] = new(library, instance, CanManageRegistrations: true),
            [listenerCredential] = new(library, listener)
        });
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var host = await StartSetupHostAsync(options, certificate, verifier, credentials);
        var path = BeetsLibrarySetupRoutes.ConfigurationPath(library);
        try
        {
            await VerifyAsync(async step =>
            {
                if (step == Step.ReadAfterRestart)
                {
                    await host.StopAsync(Token);
                    await host.DisposeAsync();
                    host = await StartSetupHostAsync(options, certificate, verifier, credentials);
                }
                var address = new Uri(host.Urls.Single());
                using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", step == Step.Unauthorized ? listenerCredential : secret);
                if (step == Step.Save)
                {
                    var defaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "desktop"), mi);
                    await using var startup = new DesktopStartup(defaults, [], library, instance,
                        host: new(address, client, new TokenProvider(library, instance, secret)), presentationScheduler: new InlineScheduler());
                    var started = Assert.IsType<Outcome<DesktopStartupState>.Success>(await startup.StartAsync(Token)).Value;
                    var ui = Assert.IsType<DesktopPresentation>(started.Presentation).HostLibrary;
                    await ui.OpenConfigurationCommand.ExecuteAsync(null);
                    var current = Assert.IsType<HostLibraryConfiguration>(ui.Configuration);
                    Assert.Equal(new[] { "info" }, current.AvailablePluginIds);
                    var accepted = Assert.IsType<Outcome<HostLibraryConfiguration>.Success>(await started.Workspace!.SendAsync(
                        new SaveHostLibraryConfiguration(library, current.Settings with { NamingTemplate = EditedNaming, EnabledPluginIds = ["info"] },
                            current.Revision), new(new(Guid.NewGuid(), instance), draft.OperationId), Token)).Value;
                    return new(200, accepted.Revision, accepted.Settings.NamingTemplate, accepted.Settings.ImportDefaults["behavior"],
                        string.Join(',', accepted.Settings.EnabledPluginIds), Hash(yaml) == original);
                }
                if (step == Step.NativeSettings)
                {
                    var runtime = host.Services.GetRequiredService<BeetsGuidedImport>();
                    var nativeConfig = await runtime.PrepareConfigAsync(Token);
                    var document = new YamlStream();
                    using (var input = File.OpenText(nativeConfig))
                        document.Load(input);
                    var values = Assert.IsType<YamlMappingNode>(Assert.Single(document.Documents).RootNode);
                    var template = Assert.IsType<YamlScalarNode>(Assert.IsType<YamlMappingNode>(values.Children["paths"]).Children["default"]).Value;
                    var plugins = Assert.IsType<YamlSequenceNode>(values.Children["plugins"]).Children.Select(node => Assert.IsType<YamlScalarNode>(node).Value);
                    var source = Directory.CreateDirectory(Path.Combine(root, "fresh-audio")).FullName;
                    await GenerateAudioAsync(ffmpeg!, Path.Combine(source, "audio.flac"), "Configuration fixture", 1, Token);
                    await ImportWithNativeProfileAsync(executable!, nativeConfig, source);
                    Assert.Single(Directory.EnumerateFiles(Path.Combine(mi, "Sorted"), "*.flac", SearchOption.AllDirectories));
                    using var read = await client.GetAsync(path, Token);
                    var settings = Assert.IsType<HostBeetsConfiguration>(await read.Content.ReadFromJsonAsync<HostBeetsConfiguration>(Token));
                    return new((int)read.StatusCode, settings.Revision, template!, settings.Settings.ImportDefaults["behavior"],
                        string.Join(',', plugins), Hash(yaml) == original);
                }

                using var reply = step switch
                {
                    Step.Unauthorized => await client.PostAsJsonAsync(path, draft with { OriginInstanceId = listener }, Token),
                    Step.DifferentCatalog => await client.PostAsJsonAsync(path, draft with { Settings = wireSettings with
                        { CatalogPath = Path.Combine(root, "another.db") } }, Token),
                    Step.UnknownPlugin => await client.PostAsJsonAsync(path, draft with { Settings = wireSettings with { EnabledPluginIds = ["unknown"] } }, Token),
                    Step.UnsupportedMatching => await client.PostAsJsonAsync(path, draft with { Settings = wireSettings with
                        { ImportDefaults = wireSettings.ImportDefaults.SetItem("behavior", "review-suggested-matches") } }, Token),
                    Step.Stale => await client.PostAsJsonAsync(path, draft with { ExpectedRevision = 7 }, Token),
                    Step.Replay => await client.PostAsJsonAsync(path, draft, Token),
                    Step.DifferentIntent => await client.PostAsJsonAsync(path, draft with { Settings = wireSettings with { NamingTemplate = "Different/$title" } }, Token),
                    _ => await client.GetAsync(path, Token)
                };
                var status = (int)reply.StatusCode;
                var preserved = Hash(yaml) == original;
                if (status is 400 or 409)
                {
                    var problem = Assert.IsType<SetupProblem>(await reply.Content.ReadFromJsonAsync<SetupProblem>(Token));
                    return new(status, problem.ActualRevision, problem.Code, BaseYamlPreserved: preserved);
                }
                if (!reply.IsSuccessStatusCode)
                    return new(status, BaseYamlPreserved: preserved);
                var actual = Assert.IsType<HostBeetsConfiguration>(await reply.Content.ReadFromJsonAsync<HostBeetsConfiguration>(Token));
                Assert.Equal(library, actual.LibraryId);
                Assert.Equal(step == Step.Replay ? draft.OperationId : (Guid?)null, actual.OperationId);
                return new(status, actual.Revision, actual.Settings.NamingTemplate, actual.Settings.ImportDefaults["behavior"],
                    string.Join(',', actual.Settings.EnabledPluginIds), preserved);
            });
            await host.StopAsync(Token);
        }
        finally { await host.DisposeAsync(); }

        // 2026-11-25: Confirm that the saved behavior controls actual discovery for FR-BEETS-001#ac-open.
        var lookupBuilder = WebApplication.CreateSlimBuilder();
        lookupBuilder.Logging.ClearProviders();
        lookupBuilder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var lookup = lookupBuilder.Build();
        var searches = 0;
        lookup.MapGet("/ws/2/release", () =>
        {
            Interlocked.Increment(ref searches);
            return new { releases = Array.Empty<object>() };
        });
        await lookup.StartAsync(Token);
        var withMatching = options with { Beets = options.Beets! with { Matching = new(ffmpeg!, new(lookup.Urls.Single())) } };
        await using var matchingHost = await StartSetupHostAsync(withMatching, certificate, verifier, credentials);
        using var writer = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(matchingHost.Urls.Single()) };
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var inputPath = Path.Combine(root, "fresh-audio", "audio.flac");
        var upload = new ImportUpload(library, instance, Guid.NewGuid(), [new("audio.flac", new FileInfo(inputPath).Length, Hash(inputPath))]);
        var stage = matchingHost.Services.GetRequiredService<FileImportStage>();
        await using (var input = File.OpenRead(inputPath))
            Assert.IsType<HostOutcome<ImportUploadPartReceipt>.Success>(await stage.CopyAsync(upload, 0, input, Token));

        var discovery = matchingHost.Services.GetRequiredService<BeetsGuidedImport>();
        var keepTags = Assert.IsType<HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success>(await discovery.DiscoverAsync(upload, Token));
        Assert.Empty(keepTags.Value);
        Assert.Equal(0, Volatile.Read(ref searches));
        var changeBehavior = draft with
        {
            ExpectedRevision = 1,
            OperationId = Guid.NewGuid(),
            Settings = wireSettings with { ImportDefaults = wireSettings.ImportDefaults.SetItem("behavior", "review-suggested-matches") }
        };
        using var review = await writer.PostAsJsonAsync(path, changeBehavior, Token);
        var reviewed = Assert.IsType<HostBeetsConfiguration>(await review.Content.ReadFromJsonAsync<HostBeetsConfiguration>(Token));
        Assert.Equal(2, reviewed.Revision);
        Assert.Equal("review-suggested-matches", reviewed.Settings.ImportDefaults["behavior"]);
        var candidates = Assert.IsType<HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success>(await discovery.DiscoverAsync(upload, Token));
        Assert.Empty(candidates.Value);
        Assert.Equal(1, Volatile.Read(ref searches));
        await matchingHost.StopAsync(Token);
        await lookup.StopAsync(Token);
    }

    private async Task ImportWithNativeProfileAsync(string executable, string configuration, string source)
    {
        var launch = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        launch.Environment["BEETSDIR"] = Path.GetDirectoryName(configuration);
        foreach (var argument in new[] { "-c", configuration, "import", "-c", "-M", "-W", "-P", "-I", "-A", "-q", "--flat", "--", source })
            launch.ArgumentList.Add(argument);
        using var process = Process.Start(launch) ?? throw new InvalidOperationException("Could not start native beets.");
        process.StandardInput.Close();
        await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, Token),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null, Token), process.WaitForExitAsync(Token));
        Assert.Equal(0, process.ExitCode);
    }
}
