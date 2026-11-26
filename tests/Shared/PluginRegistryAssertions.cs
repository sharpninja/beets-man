using BeetsMan.Desktop.Application.Extensions;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks version-scoped local trust and retirement of earlier enabled versions.</summary>
/// <remarks>2026-11-25: Independent assertions for FR-PLUGINS-001#ac-manage and TEST-PLUGINS-001.</remarks>
internal static class PluginRegistryAssertions
{
    internal static void VerifyInstalled(PluginState state, string id, string version, PluginTrust trust, bool enabled)
    {
        var plugin = Assert.Single(state.Plugins, item => item.PluginId == id && item.Version == version);
        Assert.Equal(trust, plugin.Trust);
        Assert.Equal(enabled, plugin.Enabled);
        Assert.Equal(PluginWorkerStatus.Stopped, plugin.Worker);
        Assert.Empty(plugin.Contributions);
    }

    internal static void VerifyUpgrade(PluginState state, string id, string oldVersion, string newVersion)
    {
        VerifyInstalled(state, id, oldVersion, PluginTrust.Local, false);
        VerifyInstalled(state, id, newVersion, PluginTrust.Local, true);
    }
}
