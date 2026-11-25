using System.Text.Json.Nodes;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies first-run, restart and repair against disposable appsettings and database files.</summary>
/// <remarks>2026-11-24: Bind TEST-CONFIG-002 to FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed class BootstrapIntegrationTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-bootstrap-");
    private readonly CancellationToken stop = TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstRunCreatesDefaultsAndAnAvailableCacheWithoutTheMusicRoot()
    {
        var defaults = Defaults();
        var router = Router(defaults);
        var initial = Get(await router.QueryAsync(new GetBootstrapState(), Context().Request, stop));
        Assert.Equal(BootstrapStatus.FirstRun, initial.Status);
        BootstrapAssertions.VerifyDefaults(initial.Proposed!);
        Assert.False(File.Exists(defaults.SettingsPath));
        Assert.False(File.Exists(defaults.DatabasePath));

        var initialized = Get(await router.SendAsync(new InitializeDesktop(initial.Proposed!), Context(), stop));
        Assert.Equal(BootstrapStatus.Ready, initialized.Status);
        Assert.Equal("music-root-unavailable", Assert.Single(initialized.Issues).Code);
        Assert.True(File.Exists(defaults.DatabasePath));
        var written = await new JsonBootstrapFile(defaults).ReadAsync(stop);
        BootstrapAssertions.VerifyDefaults(written.Settings!);
        Assert.Equal(defaults.DatabasePath, written.Settings!.DatabasePath);
        Assert.Equal(defaults.MusicRoot, written.Settings.MusicRoot);
        Assert.Equal(14, written.Settings.RollingLog?.RetainedFileCount);
        Assert.False(written.NeedsDefaults);
    }

    [Fact]
    public async Task CustomDatabaseRequiresExactApprovalAndRejectsSharedLocations()
    {
        var defaults = Defaults();
        var shared = Path.Combine(directory.FullName, "mirror");
        var router = Router(defaults, shared);
        var initial = Get(await router.QueryAsync(new GetBootstrapState(), Context().Request, stop));
        var proposed = defaults with { DatabasePath = Path.Combine(directory.FullName, "custom.db") };
        var command = new SaveBootstrapSettings(proposed, initial.Revision);
        var pending = Get(await router.SendAsync(command, Context(), stop));
        BootstrapAssertions.VerifyProposal(pending, proposed.DatabasePath);
        Assert.False(File.Exists(proposed.DatabasePath));
        var wrong = Get(await router.SendAsync(command with { CreationApproval = new(Guid.NewGuid(), proposed.DatabasePath) }, Context(), stop));
        BootstrapAssertions.VerifyProposal(wrong, proposed.DatabasePath);
        Assert.False(File.Exists(proposed.DatabasePath));

        var rejected = await router.SendAsync(command with { Settings = defaults with { DatabasePath = Path.Combine(shared, "not-local.db") } }, Context(), stop);
        Assert.Equal("invalid-database-path", Assert.Single(Assert.IsType<Outcome<BootstrapState>.Invalid>(rejected).Issues).Code);
        var approved = Get(await router.SendAsync(command with { CreationApproval = pending.CreationProposal }, Context(), stop));
        Assert.Equal(BootstrapStatus.Ready, approved.Status);
        Assert.True(File.Exists(proposed.DatabasePath));
    }

    [Fact]
    public async Task ExplicitAndUnknownSettingsSurviveMissingDefaultBackfillAndRestart()
    {
        var defaults = Defaults();
        var startup = Router(defaults);
        Get(await startup.SendAsync(new InitializeDesktop(defaults), Context(), stop));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(defaults.SettingsPath, stop))!.AsObject();
        json["futureSetting"] = new JsonObject { ["enabled"] = true };
        json["Logging"]!["LogLevel"]!["Default"] = "Debug";
        json["Logging"]!["LogLevel"]!.AsObject().Remove("Microsoft");
        await File.WriteAllTextAsync(defaults.SettingsPath, json.ToJsonString(), stop);

        var restart = Router(defaults);
        var ready = Get(await restart.SendAsync(new RetryDesktopBootstrap(), Context(), stop));
        Assert.Equal("Debug", ready.Effective?.LogLevels["Default"]);
        Assert.Equal("Warning", ready.Effective?.LogLevels["Microsoft"]);
        var upgraded = JsonNode.Parse(await File.ReadAllTextAsync(defaults.SettingsPath, stop))!;
        Assert.True(upgraded["futureSetting"]!["enabled"]!.GetValue<bool>());

        var current = ready.Effective!;
        var settings = current with { LogLevels = current.LogLevels.SetItem("Default", "Error") };
        var saved = Get(await restart.SendAsync(new SaveBootstrapSettings(settings, ready.Revision), Context(), stop));
        Assert.Equal(BootstrapStatus.RestartRequired, saved.Status);
        Assert.Equal("Debug", saved.Effective?.LogLevels["Default"]);
        Assert.Equal("Error", saved.Proposed?.LogLevels["Default"]);
        var restarted = Get(await Router(defaults).SendAsync(new RetryDesktopBootstrap(), Context(), stop));
        Assert.Equal("Error", restarted.Effective?.LogLevels["Default"]);

        await File.WriteAllTextAsync(defaults.SettingsPath, "{ broken", stop);
        var repair = Get(await Router(defaults).SendAsync(new RetryDesktopBootstrap(), Context(), stop));
        Assert.Equal(BootstrapStatus.RepairRequired, repair.Status);
        Assert.Equal("malformed-configuration", Assert.Single(repair.Issues).Code);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(defaults.SettingsPath, stop));
    }

    public void Dispose() => directory.Delete(recursive: true);

    private BootstrapSettings Defaults() => JsonBootstrapFile.ResolveDefaults(directory.FullName, Path.Combine(directory.FullName, "Music"));
    private static RequestDispatcher Router(BootstrapSettings defaults, params string[] shares) =>
        new BootstrapHandlers(new JsonBootstrapFile(defaults), new DesktopBootstrapEnvironment(shares)).Register(new()).Build(typeof(GetBootstrapState));
    private static CommandContext Context() => new(new(Guid.NewGuid(), null), Guid.NewGuid());
    private static BootstrapState Get(Outcome<BootstrapState> outcome) => Assert.IsType<Outcome<BootstrapState>.Success>(outcome).Value;
}
