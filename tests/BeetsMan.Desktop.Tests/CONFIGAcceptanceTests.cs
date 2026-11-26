using System.Text.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercises the named configuration contracts without opening a file or LiteDB store.</summary>
/// <remarks>2026-11-26: Bind TEST-CONFIG-001 to FR-CONFIG-001#ac-defaults, FR-CONFIG-001#ac-overrides, FR-CONFIG-001#ac-repair and FR-CONFIG-001#ac-logs.</remarks>
public sealed class CONFIGAcceptanceTests
{
    private static readonly string Profile = Path.Combine(AppContext.BaseDirectory, "fixture-profile");
    private static readonly string Music = Path.Combine(Profile, "Music");
    private static readonly CommandContext Context = new(new(Guid.NewGuid(), null), Guid.NewGuid());
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    /// <summary>Checks built-in paths and filters, and observes the first approved write at the bootstrap boundary.</summary>
    [Fact]
    public async Task Defaults_MatchesContract()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(Profile, Music);
        var file = new ConfigurationFixture(defaults, new("absent", false, null, false, []));
        var environment = new BootstrapEnvironment();
        var handlers = new BootstrapHandlers(file, environment);
        var first = Success(await handlers.HandleAsync(new GetBootstrapState(), Context.Request, token));
        Assert.Equal(BootstrapStatus.FirstRun, first.Status);
        Assert.Null(file.Written);
        var saved = Success(await handlers.HandleAsync(new InitializeDesktop(first.Proposed!), Context, token));
        Assert.Equal(BootstrapStatus.Ready, saved.Status);
        BootstrapAssertions.VerifyDefaults(saved.Effective!);
        Assert.Equal(Path.Combine(Profile, "appsettings.json"), saved.Effective!.SettingsPath);
        Assert.Equal(Path.Combine(Profile, "data", "beets-man.db"), saved.Effective.DatabasePath);
        Assert.Equal(Music, saved.Effective.MusicRoot);
        Assert.Equal(new RollingLogSettings(true, Path.Combine(Profile, "logs"), 14), saved.Effective.RollingLog);
        Assert.Equal(saved.Effective, file.Written);
        Assert.Equal((saved.Effective.DatabasePath, true), Assert.Single(environment.Checks));
    }

    /// <summary>Applies only the explicitly configured paths and filters after resume and backfills missing defaults.</summary>
    [Fact]
    public async Task Overrides_MatchesContract()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(Profile, Music);
        var configured = defaults with { DatabasePath = Path.Combine(Profile, "custom.db"),
            MusicRoot = Path.Combine(Profile, "Custom Music"), LogLevels = defaults.LogLevels.SetItem("Default", "Debug") };
        var file = new ConfigurationFixture(defaults, new("configured", true, configured, true, []));
        var environment = new BootstrapEnvironment { Exists = true };
        var handlers = new BootstrapHandlers(file, environment);
        var ready = Success(await handlers.HandleAsync(new RetryDesktopBootstrap(), Context, token));
        Assert.Equal(BootstrapStatus.Ready, ready.Status);
        Assert.Equal(configured, ready.Effective);
        Assert.Equal(configured, file.Written);
        Assert.Equal((configured.DatabasePath, false), Assert.Single(environment.Checks));
        Assert.Equal("Debug", ready.Effective!.LogLevels["Default"]);
        Assert.Equal("Warning", ready.Effective.LogLevels["Microsoft"]);
    }

    /// <summary>Preserves malformed settings, rejects a shared path and requests approval for a missing private database.</summary>
    [Fact]
    public async Task Repair_MatchesContract()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(Profile, Music);
        var file = new ConfigurationFixture(defaults, new("broken", true, null, false, [new("malformed-configuration")]));
        var environment = new BootstrapEnvironment { Shared = Path.Combine(Profile, "shared.db") };
        var handlers = new BootstrapHandlers(file, environment);
        var malformed = Success(await handlers.HandleAsync(new RetryDesktopBootstrap(), Context, token));
        Assert.Equal(BootstrapStatus.RepairRequired, malformed.Status);
        Assert.Equal("malformed-configuration", Assert.Single(malformed.Issues).Code);
        file.Snapshot = new("shared", true, defaults with { DatabasePath = environment.Shared }, false, []);
        var invalid = Success(await handlers.HandleAsync(new RetryDesktopBootstrap(), Context, token));
        Assert.Equal(BootstrapStatus.RepairRequired, invalid.Status);
        Assert.Equal("invalid-database-path", Assert.Single(invalid.Issues).Code);
        file.Snapshot = new("missing", true, defaults, false, []);
        var pending = Success(await handlers.HandleAsync(new RetryDesktopBootstrap(), Context, token));
        BootstrapAssertions.VerifyProposal(pending, defaults.DatabasePath);
        Assert.Null(file.Written);
        Assert.Empty(environment.Checks);
    }

    /// <summary>Checks structured diagnostics, bounded events and redaction without using the rolling file sink.</summary>
    [Fact]
    public async Task Logs_MatchesContract()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(Profile, Music) with { RollingLog = null };
        var clock = new FixtureClock();
        using var provider = new DiagnosticsLoggerProvider(settings, clock, eventsPerSecond: 4);
        DiagnosticsAssertions.EmitEvents(input =>
        {
            var logger = provider.CreateLogger(input.Category);
            using var scope = logger.BeginScope(new DiagnosticScope(input.CorrelationId, input.Plugin));
            logger.Log(Enum.Parse<LogLevel>(input.Level), new EventId(input.EventId, DiagnosticsAssertions.PrivateTitle),
                DiagnosticsAssertions.CliArgument, new InvalidOperationException(DiagnosticsAssertions.PrivateTitle),
                static (_, _) => throw new InvalidOperationException("Diagnostic metadata only."));
        }, () => clock.Advance(TimeSpan.FromSeconds(1)));
        var page = Success(await provider.HandleAsync(new GetDiagnostics(new()), Context.Request, token));
        DiagnosticsAssertions.Verify(page.Entries, JsonSerializer.Serialize(page));
    }

    private static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    private sealed class ConfigurationFixture(BootstrapSettings defaults, BootstrapFileRead snapshot) : IBootstrapFile
    {
        public BootstrapSettings Defaults { get; } = defaults;
        public BootstrapFileRead Snapshot { get; set; } = snapshot;
        public BootstrapSettings? Written { get; private set; }
        public Task<BootstrapFileRead> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

        public Task<Outcome<BootstrapFileRead>> WriteAsync(BootstrapSettings settings, string expectedStamp, CancellationToken cancellationToken)
        {
            Assert.Equal(Snapshot.Stamp, expectedStamp);
            Written = settings;
            Snapshot = new("saved", true, settings, false, []);
            return Task.FromResult<Outcome<BootstrapFileRead>>(new Outcome<BootstrapFileRead>.Success(Snapshot));
        }
    }

    private sealed class BootstrapEnvironment : IBootstrapEnvironment
    {
        public bool Exists { get; init; }
        public string Shared { get; init; } = string.Empty;
        public List<(string Path, bool Create)> Checks { get; } = [];
        public bool IsShared(string absolutePath) => absolutePath == Shared;
        public bool DatabaseExists(string absolutePath) => Exists;
        public bool MusicAvailable(string absolutePath) => true;

        public Task<ValidationIssue?> ValidateDatabaseAsync(string absolutePath, bool allowCreate, CancellationToken cancellationToken)
        {
            Checks.Add((absolutePath, allowCreate));
            return Task.FromResult<ValidationIssue?>(null);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DiagnosticsAssertions.Start + elapsed;
        public void Advance(TimeSpan interval) => elapsed += interval;
    }
}
