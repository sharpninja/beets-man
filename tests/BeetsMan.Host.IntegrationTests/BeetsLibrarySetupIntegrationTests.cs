using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using YamlDotNet.Serialization;
using static BeetsMan.Host.Tests.BeetsLibrarySetupAssertions;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Opens a native library and initializes another through the real hosted registry and setup routes.</summary>
    /// <remarks>2026-11-25: Verify STB08 scope, byte preservation, native creation and restart for TEST-BEETS-002.</remarks>
    /// <remarks>2026-11-26: Verify authorized opening and native initialization for FR-BEETS-001#ac-open.</remarks>
    /// <remarks>2026-11-26: Reuse the existing native fixture from the named FR-BEETS-001#ac-open case.</remarks>
    internal async Task BeetsLibrarySetupOpensExistingAndInstallsTheApprovedNativeLibrary()
    {
        var executable = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS") ?? (OperatingSystem.IsLinux() ? "/usr/bin/beet" : null);
        Assert.True(executable is not null && File.Exists(executable), "Set BEETSMAN_TEST_BEETS to the installed beets executable.");
        const string template = "$albumartist/$album/$title";
        const string listenerCredential = "fixture-beets-setup-listener";
        const string unregisteredCredential = "fixture-beets-setup-unregistered";
        var listener = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var original = HostOptions("existing");
        var fresh = HostOptions("new");
        var native = original.Beets!;
        await PrepareNativeFixtureAsync(executable!, native.ConfigurationPath!, native.CatalogPath, original.MusicRoot);
        var originalCatalogHash = Hash(native.CatalogPath);
        var originalConfigHash = Hash(native.ConfigurationPath!);
        using var certificate = CreateCertificate();
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        {
            [secret] = new(library, instance, CanManageRegistrations: true),
            [listenerCredential] = new(library, listener),
            [unregisteredCredential] = new(library, outsider, CanManageRegistrations: true)
        });
        var nativeCredentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        await using var existing = await StartSetupHostAsync(original, certificate, verifier, nativeCredentials);
        var host = await StartSetupHostAsync(fresh, certificate, verifier, nativeCredentials);
        var operation = Guid.NewGuid();
        var originalSettings = Settings(original);
        var freshSettings = Settings(fresh);
        var existingRequest = new InitializeHostBeetsRequest(instance, Guid.NewGuid(), 0, originalSettings);
        var newRequest = new InitializeHostBeetsRequest(instance, operation, 0, freshSettings);
        var path = BeetsLibrarySetupRoutes.StatePath(library);
        var configurationPath = BeetsLibrarySetupRoutes.ConfigurationPath(library);
        try
        {
            await VerifyAsync(async step =>
            {
                if (step == Step.OpenAfterRestart)
                {
                    await host.StopAsync(Token);
                    await host.DisposeAsync();
                    host = await StartSetupHostAsync(fresh, certificate, verifier, nativeCredentials);
                }
                var existingCase = step is Step.OpenExisting or Step.ReadExistingSettings or Step.InitializeExisting;
                var target = existingCase ? original : fresh;
                var address = new Uri((existingCase ? existing : host).Urls.Single());
                var credential = step switch
                {
                    Step.UnregisteredRead => unregisteredCredential,
                    Step.ListenerInitialize => listenerCredential,
                    _ => secret
                };
                using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                if (step == Step.Initialize)
                {
                    var defaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "desktop"), target.MusicRoot);
                    await using var startup = new DesktopStartup(defaults, [], library, instance,
                        host: new(address, client, new TokenProvider(library, instance, secret)), presentationScheduler: new InlineScheduler());
                    var running = Assert.IsType<Outcome<DesktopStartupState>.Success>(await startup.StartAsync(Token)).Value;
                    var presentation = Assert.IsType<DesktopPresentation>(running.Presentation);
                    await presentation.HostLibrary.OpenLibraryCommand.ExecuteAsync(null);
                    await presentation.HostLibrary.OpenConfigurationCommand.ExecuteAsync(null);
                    var draft = Assert.IsType<HostLibraryConfiguration>(presentation.HostLibrary.Configuration);
                    var initial = Assert.IsType<HostLibraryState>(presentation.HostLibrary.Library);
                    Assert.False(initial.IsInitialized);
                    Assert.Equal(freshSettings.ConfigurationPath, draft.Settings.ConfigurationPath);
                    var result = Assert.IsType<Outcome<HostLibraryState>.Success>(await running.Workspace!.SendAsync(
                        new InitializeHostLibrary(library, draft.Settings, draft.Revision),
                        new(new(Guid.NewGuid(), instance), operation), Token)).Value;
                    return new(200, result.Revision, result.IsInitialized, result.CanEdit, ConfigurationMatches(target), CatalogPresent(target));
                }

                using var reply = step switch
                {
                    Step.ReadExistingSettings => await client.GetAsync(configurationPath, Token),
                    Step.InitializeExisting => await client.PostAsJsonAsync(path, existingRequest, Token),
                    Step.ListenerInitialize => await client.PostAsJsonAsync(path, newRequest with { OriginInstanceId = listener }, Token),
                    Step.UnapprovedDestination => await client.PostAsJsonAsync(path, newRequest with
                    {
                        Settings = newRequest.Settings with { CatalogPath = Path.Combine(root, "unapproved.db") }
                    }, Token),
                    Step.StaleInitialization => await client.PostAsJsonAsync(path, newRequest with { ExpectedRevision = 9 }, Token),
                    Step.RetryInitialization => await client.PostAsJsonAsync(path, newRequest, Token),
                    _ => await client.GetAsync(path, Token)
                };
                if (step == Step.UnapprovedDestination)
                    Assert.False(File.Exists(Path.Combine(root, "unapproved.db")));
                var preserved = !existingCase || originalCatalogHash == Hash(native.CatalogPath) &&
                    originalConfigHash == Hash(native.ConfigurationPath!);
                var present = CatalogPresent(target);
                var status = (int)reply.StatusCode;
                if (status is 400 or 409)
                {
                    var issue = Assert.IsType<SetupProblem>(await reply.Content.ReadFromJsonAsync<SetupProblem>(Token));
                    return new(status, issue.ActualRevision, CatalogPresent: present, ExistingBytesPreserved: preserved, Issue: issue.Code);
                }
                if (!reply.IsSuccessStatusCode)
                    return new(status, CatalogPresent: present, ExistingBytesPreserved: preserved);
                if (step == Step.ReadExistingSettings)
                {
                    var settings = Assert.IsType<HostBeetsConfiguration>(await reply.Content.ReadFromJsonAsync<HostBeetsConfiguration>(Token));
                    var expected = Settings(target);
                    var current = settings.Settings;
                    return new(status, settings.Revision, CanEdit: settings.CanEdit,
                        ConfigurationMatches: current.CatalogPath == expected.CatalogPath && current.ConfigurationPath == expected.ConfigurationPath &&
                            current.SourceMusicRoot == expected.SourceMusicRoot && current.NamingTemplate == template &&
                            current.ImportDefaults["behavior"] == "review-suggested-matches" &&
                            current.EnabledPluginIds.IsEmpty && ConfigurationMatches(target),
                        CatalogPresent: present, ExistingBytesPreserved: preserved);
                }
                var state = Assert.IsType<HostBeetsLibraryState>(await reply.Content.ReadFromJsonAsync<HostBeetsLibraryState>(Token));
                Assert.Equal(library, state.LibraryId);
                Assert.Equal(step == Step.RetryInitialization ? operation : (Guid?)null, state.OperationId);
                return new(status, state.Revision, state.IsInitialized, state.CanEdit,
                    step is Step.RetryInitialization or Step.OpenAfterRestart && ConfigurationMatches(target), present, preserved);
            });
            await host.StopAsync(Token);
        }
        finally { await host.DisposeAsync(); }

        LibraryHostOptions HostOptions(string label)
        {
            var hostRoot = Directory.CreateDirectory(Path.Combine(root, label)).FullName;
            var store = Path.Combine(hostRoot, "projection.db");
            SeedHost(store);
            using (var database = new LiteDatabase(store))
                database.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                    { ["_id"] = listener, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            return new(library, store, Directory.CreateDirectory(Path.Combine(hostRoot, "music")).FullName,
                Path.Combine(hostRoot, "uploads"), Path.Combine(hostRoot, "work"), [],
                Beets: new(executable!, Path.Combine(hostRoot, "catalog.db"), template, true,
                    Matching: label == "existing" ? new(Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG") ?? executable!) : null,
                    ConfigurationPath: Path.Combine(hostRoot, "config.yaml")));
        }

        static HostBeetsSettings Settings(LibraryHostOptions options) => new(options.Beets!.CatalogPath, options.MusicRoot,
            template, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("behavior",
                options.Beets.Matching is null ? "keep-tags" : "review-suggested-matches"), [], options.Beets.ConfigurationPath!);
    }

    private async Task<WebApplication> StartSetupHostAsync(LibraryHostOptions options, X509Certificate2 certificate,
        ReplicaFixtureVerifier verifier, MirrorNativeCredentials credentials)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var app = LibraryHostStartup.Build(builder, options, verifier, credentials, credentials);
        try
        {
            await app.StartAsync(Token);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private async Task PrepareNativeFixtureAsync(string executable, string config, string catalog, string music)
    {
        await File.WriteAllTextAsync(config, new SerializerBuilder().Build().Serialize(new Dictionary<string, object>
        {
            ["library"] = catalog, ["directory"] = music, ["plugins"] = Array.Empty<string>()
        }), Token);
        var launch = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        launch.Environment["BEETSDIR"] = Path.GetDirectoryName(config);
        foreach (var argument in new[] { "-c", config, "list" })
            launch.ArgumentList.Add(argument);
        using var process = Process.Start(launch) ?? throw new InvalidOperationException("Could not start native beets.");
        await Task.WhenAll(process.StandardError.BaseStream.CopyToAsync(Stream.Null, Token),
            process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, Token), process.WaitForExitAsync(Token));
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(catalog));
    }

    private static bool ConfigurationMatches(LibraryHostOptions options)
    {
        var file = options.Beets!.ConfigurationPath!;
        if (!File.Exists(file))
            return false;
        var config = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
        return Equals(config["library"], options.Beets.CatalogPath) && Equals(config["directory"], options.MusicRoot);
    }

    private static bool CatalogPresent(LibraryHostOptions options) => File.Exists(options.Beets!.CatalogPath) &&
        File.ReadAllBytes(options.Beets.CatalogPath).AsSpan().StartsWith("SQLite format 3\0"u8);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record SetupProblem(string Code, long? ActualRevision);
}
