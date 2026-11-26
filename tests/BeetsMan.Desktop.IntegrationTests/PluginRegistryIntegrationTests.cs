using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises actual package admission and local registry persistence through desktop startup and CQRS.</summary>
/// <remarks>2026-11-25: Bind TEST-PLUGINS-001 and FR-PLUGINS-001#ac-manage to local ZIP and LiteDB adapters.</remarks>
public sealed class PLUGINSAcceptanceTests : IDisposable
{
    private const string Plugin = "example.visualizer";
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-plugin-registry-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;
    private readonly Guid library = Guid.NewGuid();
    private readonly Guid origin = Guid.NewGuid();

    /// <summary>Installs, trusts, enables, upgrades, disables and removes the exact selected package version.</summary>
    /// <remarks>2026-11-26: Bind the named package management case for FR-PLUGINS-001#ac-manage.</remarks>
    [Fact]
    public async Task Manage_MatchesContract()
    {
        var first = Package("first", "1.0.0");
        var second = Package("second", "2.0.0");
        var altered = Package("changed", "1.0.0");
        var path = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var context = new RequestContext(Guid.NewGuid(), origin);
        await using var desktop = new DesktopStartup(path, [], library, origin, _ => new UnusedOutput(), pluginRecoveryMode: true);
        var ready = Value(await desktop.StartAsync(token));
        var tools = Assert.IsType<LocalPluginPackages>(ready.PluginPackages);
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(ready.Workspace);
        var selected = tools.SelectPackage(first);
        var firstInstalled = Value(await Submit(new InstallPluginPackage(selected)));
        PluginRegistryAssertions.VerifyInstalled(firstInstalled, Plugin, "1.0.0", PluginTrust.Untrusted, false);
        Assert.Equal("plugin-local-trust-required", Issue(await Submit(new SetPluginEnabled(Plugin, "1.0.0", true))));
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first, token)));
        var archiveCopy = Path.Combine(root.FullName, "plugins", "archives", $"{digest}.zip");
        Assert.Equal(await File.ReadAllBytesAsync(first, token), await File.ReadAllBytesAsync(archiveCopy, token));
        Assert.Equal(firstInstalled.Revision, Value(await Submit(new InstallPluginPackage(tools.SelectPackage(first)))).Revision);
        Assert.Equal("plugin-version-content-changed", Assert.IsType<Outcome<PluginState>.Conflict>(
            await Submit(new InstallPluginPackage(tools.SelectPackage(altered)))).Code);
        Value(await Submit(new SetPluginTrust(Plugin, "1.0.0", PluginTrust.Local)));
        Value(await Submit(new SetPluginEnabled(Plugin, "1.0.0", true)));
        var update = Value(await Submit(new InstallPluginPackage(tools.SelectPackage(second))));
        PluginRegistryAssertions.VerifyInstalled(update, Plugin, "1.0.0", PluginTrust.Local, true);
        PluginRegistryAssertions.VerifyInstalled(update, Plugin, "2.0.0", PluginTrust.Untrusted, false);
        Assert.Equal("plugin-local-trust-required", Issue(await Submit(new SetPluginEnabled(Plugin, "2.0.0", true))));
        Value(await Submit(new SetPluginTrust(Plugin, "2.0.0", PluginTrust.Local)));
        var enabled = Value(await Submit(new SetPluginEnabled(Plugin, "2.0.0", true)));
        PluginRegistryAssertions.VerifyUpgrade(enabled, Plugin, "1.0.0", "2.0.0");
        Value(await desktop.StopAsync(token));

        var restarted = Value(await desktop.StartAsync(token));
        bus = Assert.IsAssignableFrom<IRequestDispatcher>(restarted.Workspace);
        var restored = Value(await bus.QueryAsync(new GetPluginState(), context, token));
        Assert.Equal(enabled.Revision, restored.Revision);
        PluginRegistryAssertions.VerifyUpgrade(restored, Plugin, "1.0.0", "2.0.0");
        Assert.Equal("plugin-package-selection-not-found", Issue(await Submit(new InstallPluginPackage(selected))));
        var disabled = Value(await Submit(new SetPluginTrust(Plugin, "2.0.0", PluginTrust.Untrusted)));
        PluginRegistryAssertions.VerifyInstalled(disabled, Plugin, "2.0.0", PluginTrust.Untrusted, false);
        var removal = Value(await Submit(new RemovePlugin(Plugin, "1.0.0")));
        Assert.DoesNotContain(removal.Plugins, item => item.Version == "1.0.0");
        Assert.True(File.Exists(first));
        Assert.Empty(Value(await bus.QueryAsync(new GetPluginState(Plugin, "1.0.0"), context, token)).Plugins);

        Task<Outcome<T>> Submit<T>(ICommand<T> command) => bus.SendAsync(command, new(context, Guid.NewGuid()), token);
    }

    /// <summary>Exercises actual worker telemetry and validates the host's responsive playback projection.</summary>
    /// <remarks>2026-11-26: Bind the named worker visualization case for FR-PLUGINS-001#ac-visualize.</remarks>
    [Fact]
    public async Task Visualize_MatchesContract()
    {
        using var worker = new PluginWorkerIntegrationTests();
        await worker.WorkerVisualizationUsesFreshAudioWhilePlaybackRemainsResponsive();
    }

    /// <summary>Verifies worker failure, responsive recovery, and startup with extensions disabled.</summary>
    /// <remarks>2026-11-26: Bind the named worker recovery case for FR-PLUGINS-001#ac-recovery.</remarks>
    [Fact]
    public async Task Recovery_MatchesContract()
    {
        using var worker = new PluginWorkerIntegrationTests();
        await worker.WorkspaceSupervisesWorkerExitRestartAndRecovery();
    }

    [Fact]
    public async Task InvalidPathsAndIncompatibleProtocolsCannotBecomeTrusted()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        await using var desktop = new DesktopStartup(settings, [], library, origin, _ => new UnusedOutput(), pluginRecoveryMode: true);
        var ready = Value(await desktop.StartAsync(token));
        var tools = Assert.IsType<LocalPluginPackages>(ready.PluginPackages);
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(ready.Workspace);
        var context = new RequestContext(Guid.NewGuid(), origin);
        var candidates = new[]
        {
            Package("traversal", "1.0.0", "../elsewhere.dll"),
            Package("absolute", "1.0.0", "/outside.dll"),
            Package("alias", "1.0.0", "PAYLOAD/WORKER.DLL"),
            Package("symlink", "1.0.0", "payload/link.dll", link: true)
        };
        foreach (var file in candidates)
            Assert.Equal("invalid-plugin-package-path", Issue(await Submit(new InstallPluginPackage(tools.SelectPackage(file)))));
        var missingKind = Package("missing-kind", "1.0.0", includeKind: false);
        Assert.Equal("invalid-plugin-package", Issue(await Submit(new InstallPluginPackage(tools.SelectPackage(missingKind)))));
        Assert.Empty(Value(await bus.QueryAsync(new GetPluginState(), context, token)).Plugins);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.FullName, "plugins", "archives")));
        var future = Package("future", "3.0.0", minimum: 2);
        var incompatible = Value(await Submit(new InstallPluginPackage(tools.SelectPackage(future))));
        Assert.False(Assert.Single(incompatible.Plugins).Compatible);
        Assert.Equal("plugin-protocol-incompatible", Issue(await Submit(new SetPluginTrust(Plugin, "3.0.0", PluginTrust.Local))));
        Assert.Equal("plugin-protocol-incompatible", Issue(await Submit(new SetPluginEnabled(Plugin, "3.0.0", true))));

        Task<Outcome<T>> Submit<T>(ICommand<T> command) => bus.SendAsync(command, new(context, Guid.NewGuid()), token);
    }

    private string Package(string name, string version, string? extraPath = null, int minimum = 1, bool link = false, bool includeKind = true)
    {
        const string entryPoint = "payload/worker.dll";
        var path = Path.Combine(root.FullName, $"{name}.zip");
        using var package = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifest = package.CreateEntry("plugin.json").Open())
            JsonSerializer.Serialize(manifest, new
            {
                pluginId = Plugin, version, entryPoint,
                minimumProtocol = minimum, maximumProtocol = minimum,
                contributions = includeKind ? (object)new[] { new { contributionId = "levels", kind = "visualization" } } :
                    new[] { new { contributionId = "levels" } }
            });
        using (var payload = new StreamWriter(package.CreateEntry(entryPoint).Open()))
            payload.Write(name);
        if (extraPath is not null)
        {
            var extra = package.CreateEntry(extraPath);
            if (link)
                extra.ExternalAttributes = unchecked((int)0xa0000000);
            using var writer = new StreamWriter(extra.Open());
            writer.Write("fixture");
        }
        return path;
    }

    public void Dispose() => root.Delete(recursive: true);

    private static T Value<T>(Outcome<T> value) => Assert.IsType<Outcome<T>.Success>(value).Value;
    private static string Issue<T>(Outcome<T> value) => Assert.Single(Assert.IsType<Outcome<T>.Invalid>(value).Issues).Code;

    private sealed class UnusedOutput : IDesktopPlaybackOutput
    {
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token) =>
            throw new InvalidOperationException("Plugin registry operations do not touch playback.");
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) =>
            throw new InvalidOperationException("Plugin registry operations do not touch playback.");
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
            throw new InvalidOperationException("Plugin registry operations do not touch playback.");
        public void Dispose() { }
    }
}
