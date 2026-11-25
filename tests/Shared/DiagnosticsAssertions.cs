using System.Collections.Immutable;
using System.Text.Json;
using BeetsMan.Desktop.Application.Configuration;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Shares event filtering and privacy assertions between fixture and real diagnostics.</summary>
/// <remarks>2026-11-24: Prove TEST-CONFIG-001 and TEST-CONFIG-002 for FR-CONFIG-001#ac-logs.</remarks>
internal static class DiagnosticsAssertions
{
    public static readonly Guid Correlation = Guid.Parse("a31db162-7c47-4679-bd04-b3dcb9d6c9f5");
    public const string CliArgument = "--authorization=seed-private-credential";
    public const string PrivateTitle = "seed-private-track-title";
    public const string PluginId = "test.media-plugin";
    public static readonly DateTimeOffset Start = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public static void EmitEvents(Action<Emission> emit, Action advance)
    {
        emit(new("BeetsMan.Playback", "Debug", 10));
        emit(new("Microsoft.Hosting", "Information", 11));
        emit(new("BeetsMan.Playback", "Information", 12, Correlation, PluginId));
        emit(new("Microsoft.Hosting", "Warning", 13));
        for (var i = 14; i < 34; i++)
            emit(new("BeetsMan.Playback", "Information", i));

        advance();
        emit(new("BeetsMan.Playback", "Error", 34));
    }

    public static void Verify(ImmutableArray<DiagnosticEntry> newestFirst, string serialized)
    {
        Assert.Equal(["34", "15", "14", "13", "12"], newestFirst.Select(entry => entry.Code));
        Assert.Equal(Start.AddSeconds(1), newestFirst[0].OccurredAt);
        var warning = Assert.Single(newestFirst, entry => entry.Category == "Microsoft.Hosting");
        Assert.Equal("Warning", warning.Level);
        Assert.Null(warning.PluginId);
        Assert.Null(warning.CorrelationId);
        var plugin = newestFirst[^1];
        Assert.Equal(Start, plugin.OccurredAt);
        Assert.Equal("BeetsMan.Playback", plugin.Category);
        Assert.Equal("Information", plugin.Level);
        Assert.Equal(Correlation, plugin.CorrelationId);
        Assert.Equal(PluginId, plugin.PluginId);
        VerifyRedaction(serialized);
    }

    public static void VerifyRedaction(string serialized)
    {
        Assert.DoesNotContain(CliArgument, serialized);
        Assert.DoesNotContain(PrivateTitle, serialized);
    }

    public static void VerifyRolling(string[] oldestToNewest)
    {
        Assert.Equal(2, oldestToNewest.Length);
        var retainedCodes = oldestToNewest.SelectMany(lines => lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(line => JsonSerializer.Deserialize<DiagnosticEntry>(line)!.Code);
        Assert.Equal(["51", "52"], retainedCodes);
        VerifyRedaction(string.Join('\n', oldestToNewest));
    }

    public sealed record Emission(string Category, string Level, int EventId, Guid? CorrelationId = null, string? Plugin = null);
}
