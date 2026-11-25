using System.Text.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Tests;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Applies diagnostics acceptance assertions to the filtered logger and real files.</summary>
/// <remarks>2026-11-24: Bind TEST-CONFIG-001 and TEST-CONFIG-002 for FR-CONFIG-001#ac-logs.</remarks>
public sealed class DiagnosticsIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-man-diagnostics-{Guid.NewGuid():N}");
    private readonly AdjustableClock clock = new();
    private BootstrapSettings Defaults => JsonBootstrapFile.ResolveDefaults(root, Path.Combine(root, "Music"));

    [Fact]
    public async Task FiltersRedactsAndRateLimitsEventsThroughTheCqrsQuery()
    {
        using var provider = new DiagnosticsLoggerProvider(Defaults, clock, eventsPerSecond: 4);
        DiagnosticsAssertions.EmitEvents(input => Emit(provider, input), () => clock.Advance(TimeSpan.FromSeconds(1)));
        var token = TestContext.Current.CancellationToken;
        var context = new RequestContext(Guid.NewGuid(), null);
        var result = await provider.HandleAsync(new(new()), context, token);
        var page = Assert.IsType<Outcome<DiagnosticsPage>.Success>(result).Value;
        DiagnosticsAssertions.Verify(page.Entries, JsonSerializer.Serialize(page));

        var filtered = Assert.IsType<Outcome<DiagnosticsPage>.Success>(await provider.HandleAsync(
            new(new("beetsman", DiagnosticsAssertions.Correlation, DiagnosticsAssertions.PluginId)), context, token));
        Assert.Equal("12", Assert.Single(filtered.Value.Entries).Code);
        var files = Directory.GetFiles(Defaults.RollingLog!.Directory);
        DiagnosticsAssertions.VerifyRedaction(File.ReadAllText(Assert.Single(files)));
    }

    [Fact]
    public void RollingFilesKeepTheLatestSegmentsAcrossRestart()
    {
        var settings = Defaults;
        settings = settings with { RollingLog = settings.RollingLog! with { RetainedFileCount = 2 } };
        var directory = settings.RollingLog.Directory;
        Directory.CreateDirectory(directory);
        var unrelated = Path.Combine(directory, "unrelated.jsonl");
        File.WriteAllText(unrelated, "keep");
        for (var id = 50; id <= 52; id++)
        {
            using var provider = new DiagnosticsLoggerProvider(settings, clock);
            Emit(provider, new("BeetsMan.Playback", "Information", id));
            clock.Advance(TimeSpan.FromDays(1));
        }

        var retained = Directory.GetFiles(directory, "beets-man-*.jsonl").Order(StringComparer.Ordinal)
            .Select(File.ReadAllText).ToArray();
        DiagnosticsAssertions.VerifyRolling(retained);
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    private static void Emit(DiagnosticsLoggerProvider provider, DiagnosticsAssertions.Emission input)
    {
        var logger = provider.CreateLogger(input.Category);
        using var privateScope = logger.BeginScope(new Dictionary<string, string> { ["Title"] = DiagnosticsAssertions.PrivateTitle });
        using var diagnosticScope = logger.BeginScope(new DiagnosticScope(input.CorrelationId, input.Plugin));
        logger.Log(Enum.Parse<LogLevel>(input.Level), new EventId(input.EventId, DiagnosticsAssertions.PrivateTitle), DiagnosticsAssertions.CliArgument,
            new InvalidOperationException(DiagnosticsAssertions.PrivateTitle), static (_, _) => throw new InvalidOperationException("A private formatter was executed."));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => DiagnosticsAssertions.Start + elapsed;
        public void Advance(TimeSpan interval) => elapsed += interval;
    }
}
