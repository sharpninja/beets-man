using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the declarative panel assertions against independent worker input.</summary>
/// <remarks>2026-11-25: Gate scoped panel input for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed class PluginPanelMockTests
{
    [Fact]
    public void DistinguishesTrustedTrackContextFromInjectedOrChangedFields()
    {
        var track = Guid.NewGuid();
        const string note = "Check the mix at 1:42";
        var input = new Dictionary<string, string> { ["track"] = track.ToString("D"), ["note"] = note };
        PluginPanelAssertions.VerifyInput(input, track, note);
        Assert.ThrowsAny<XunitException>(() => PluginPanelAssertions.VerifyInput(input, Guid.NewGuid(), note));
        Assert.ThrowsAny<XunitException>(() => PluginPanelAssertions.VerifyInput(input, track, "Changed note"));
        Assert.ThrowsAny<XunitException>(() => PluginPanelAssertions.VerifyInput(
            new Dictionary<string, string>(input) { ["unexpected"] = "" }, track, note));
    }
}
