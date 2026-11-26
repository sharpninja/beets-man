using BeetsMan.Desktop.Application.Extensions;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves worker identity, negotiated contribution and failure assertions independently.</summary>
/// <remarks>2026-11-25: Gate production worker negotiation for TEST-PLUGINS-001.</remarks>
/// <remarks>2026-11-26: Verify bounded contributions and worker failure assertions for FR-PLUGINS-001#ac-visualize and FR-PLUGINS-001#ac-recovery.</remarks>
public sealed class PluginWorkerMockTests
{
    [Fact]
    public void RejectsADeadWorkerThatRemainsVisibleOrReusesItsReadyRevision()
    {
        var live = new PluginInfo("example.visualizer", "1.0.0", PluginTrust.Local, true, PluginWorkerStatus.Ready,
            [new("levels", PluginContributionKind.Visualization)]);
        var failed = live with { Worker = PluginWorkerStatus.Failed, FailureCode = "plugin-worker-exited", Contributions = [] };
        var previous = new PluginState(10, [live]);
        PluginWorkerAssertions.VerifyExit(previous, new(11, [failed]));
        Assert.ThrowsAny<XunitException>(() => PluginWorkerAssertions.VerifyExit(previous, new(10, [failed])));
        Assert.ThrowsAny<XunitException>(() => PluginWorkerAssertions.VerifyExit(previous, new(11, [failed with { Contributions = live.Contributions }])));
        Assert.ThrowsAny<XunitException>(() => PluginWorkerAssertions.VerifyExit(previous, new(11, [live])));
    }

    [Fact]
    public void RejectsUnexpectedIdentityAndExposedFailedContributions()
    {
        var contribution = new PluginContribution("levels", PluginContributionKind.Visualization);
        var ready = new PluginInfo("example.visualizer", "1.0.0", PluginTrust.Local, true, PluginWorkerStatus.Ready, [contribution]);
        var failed = ready with { Worker = PluginWorkerStatus.Failed, Contributions = [], FailureCode = "plugin-worker-protocol-invalid" };
        void VerifyReady(PluginInfo observed) => PluginWorkerAssertions.Verify(observed, "example.visualizer", "1.0.0",
            PluginWorkerStatus.Ready, null, contribution);
        void VerifyFailed(PluginInfo observed) => PluginWorkerAssertions.Verify(observed, "example.visualizer", "1.0.0",
            PluginWorkerStatus.Failed, "plugin-worker-protocol-invalid");
        VerifyReady(ready);
        VerifyFailed(failed);
        Assert.ThrowsAny<XunitException>(() => VerifyReady(ready with { Version = "2.0.0" }));
        Assert.ThrowsAny<XunitException>(() => VerifyReady(ready with { Contributions = [contribution with { Kind = PluginContributionKind.Action }] }));
        Assert.ThrowsAny<XunitException>(() => VerifyFailed(failed with { Contributions = [contribution] }));
        Assert.ThrowsAny<XunitException>(() => VerifyFailed(failed with { FailureCode = "plugin-worker-exited" }));
    }
}
