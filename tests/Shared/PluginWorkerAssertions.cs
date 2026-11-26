using BeetsMan.Desktop.Application.Extensions;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the exact contribution set the host may expose from a negotiated worker.</summary>
/// <remarks>2026-11-25: Cover FR-PLUGINS-001#ac-visualize and ac-recovery with TEST-PLUGINS-001/002.</remarks>
internal static class PluginWorkerAssertions
{
    internal static void Verify(PluginInfo worker, string id, string version, PluginWorkerStatus status, string? failure,
        params PluginContribution[] contributions)
    {
        Assert.Equal(id, worker.PluginId);
        Assert.Equal(version, worker.Version);
        Assert.Equal(PluginTrust.Local, worker.Trust);
        Assert.True(worker.Enabled);
        Assert.Equal(status, worker.Worker);
        Assert.Equal(failure, worker.FailureCode);
        Assert.Equal(contributions, worker.Contributions.ToArray());
    }

    /// <summary>Checks that worker exit detaches its contributions and advances the visible registry revision.</summary>
    /// <remarks>2026-11-25: Exercise worker isolation for FR-PLUGINS-001#ac-recovery.</remarks>
    internal static void VerifyExit(PluginState ready, PluginState failed)
    {
        var before = Assert.Single(ready.Plugins);
        Assert.Equal(PluginWorkerStatus.Ready, before.Worker);
        Assert.NotEmpty(before.Contributions);
        Assert.True(failed.Revision > ready.Revision);
        Verify(Assert.Single(failed.Plugins), before.PluginId, before.Version, PluginWorkerStatus.Failed, "plugin-worker-exited");
    }
}
