using System.Collections.Immutable;
using System.Text.Json;
using BeetsMan.Desktop.Application.Configuration;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the diagnostics acceptance assertions against independent fixture responses.</summary>
/// <remarks>2026-11-24: Exercise correct and incorrect TEST-CONFIG-001/002 responses for FR-CONFIG-001#ac-logs.</remarks>
public sealed class DiagnosticsMockTests
{
    [Fact]
    public void StructuredDiagnosticsRejectLeakedFieldsAndUnboundedEvents()
    {
        var inputs = new List<DiagnosticsAssertions.Emission>();
        var elapsed = TimeSpan.Zero;
        DiagnosticsAssertions.EmitEvents(inputs.Add, () => elapsed += TimeSpan.FromSeconds(1));
        Assert.Equal(25, inputs.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
        ImmutableArray<DiagnosticEntry> expected =
        [
            new(DiagnosticsAssertions.Start + elapsed, "Error", "BeetsMan.Playback", "34", null, null),
            new(DiagnosticsAssertions.Start, "Information", "BeetsMan.Playback", "15", null, null),
            new(DiagnosticsAssertions.Start, "Information", "BeetsMan.Playback", "14", null, null),
            new(DiagnosticsAssertions.Start, "Warning", "Microsoft.Hosting", "13", null, null),
            new(DiagnosticsAssertions.Start, "Information", "BeetsMan.Playback", "12", DiagnosticsAssertions.Correlation,
                DiagnosticsAssertions.PluginId)
        ];

        var serialized = JsonSerializer.Serialize(expected);
        DiagnosticsAssertions.Verify(expected, serialized);
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.Verify(expected.Add(expected[1] with { Code = "16" }), serialized));
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.Verify(expected.SetItem(3, expected[3] with { Level = "Information" }), serialized));
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.Verify(expected.SetItem(4, expected[4] with { PluginId = null }), serialized));
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.Verify(expected, serialized + DiagnosticsAssertions.CliArgument));
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.Verify(expected, serialized + DiagnosticsAssertions.PrivateTitle));
    }

    [Fact]
    public void RetentionRejectsAnExpiredFile()
    {
        string FileFor(string code) => JsonSerializer.Serialize(new DiagnosticEntry(DiagnosticsAssertions.Start,
            "Information", "BeetsMan.Playback", code, null, null));

        DiagnosticsAssertions.VerifyRolling([FileFor("51"), FileFor("52")]);
        Assert.ThrowsAny<XunitException>(() => DiagnosticsAssertions.VerifyRolling([FileFor("50"), FileFor("51"), FileFor("52")]));
    }
}
