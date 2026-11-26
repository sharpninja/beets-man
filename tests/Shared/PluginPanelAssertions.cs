using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the exact user input and host-owned context delivered by a plugin panel.</summary>
/// <remarks>2026-11-25: Cover the declarative panel path for FR-PLUGINS-001#ac-visualize and TEST-PLUGINS-001.</remarks>
internal static class PluginPanelAssertions
{
    internal static void VerifyInput(IReadOnlyDictionary<string, string> arguments, Guid track, string note)
    {
        Assert.Equal(2, arguments.Count);
        Assert.Equal(track.ToString("D"), arguments["track"]);
        Assert.Equal(note, arguments["note"]);
    }
}
