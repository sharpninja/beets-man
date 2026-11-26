using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the plugin registry assertions through an independent scripted command result.</summary>
/// <remarks>2026-11-25: Exercise version-scoped trust and enablement for FR-PLUGINS-001#ac-manage.</remarks>
public sealed class PluginRegistryMockTests
{
    [Fact]
    public async Task DetectsInheritedTrustAndSimultaneousActiveVersions()
    {
        const string id = "example.visualizer";
        var old = new PluginInfo(id, "1.0.0", PluginTrust.Local, true, PluginWorkerStatus.Stopped, []);
        var update = new PluginInfo(id, "2.0.0", PluginTrust.Untrusted, false, PluginWorkerStatus.Stopped, []);
        PluginRegistryAssertions.VerifyInstalled(new(3, [old, update]), id, "2.0.0", PluginTrust.Untrusted, false);
        Assert.ThrowsAny<XunitException>(() => PluginRegistryAssertions.VerifyInstalled(new(3, [old, update with { Trust = PluginTrust.Local }]),
            id, "2.0.0", PluginTrust.Untrusted, false));
        Assert.ThrowsAny<XunitException>(() => PluginRegistryAssertions.VerifyInstalled(new(3, [old, update with { Worker = PluginWorkerStatus.Ready }]),
            id, "2.0.0", PluginTrust.Untrusted, false));
        var promoted = update with { Trust = PluginTrust.Local, Enabled = true };
        var response = new PluginState(5, [old with { Enabled = false }, promoted]);
        var bus = new RequestDispatcher.Builder().RegisterCommand<SetPluginEnabled, PluginState>(new ScriptedEnable(response)).Build();
        var actual = Assert.IsType<Outcome<PluginState>.Success>(await bus.SendAsync(new SetPluginEnabled(id, "2.0.0", true),
            new(new(Guid.NewGuid(), null), Guid.NewGuid()), TestContext.Current.CancellationToken)).Value;
        PluginRegistryAssertions.VerifyUpgrade(actual, id, "1.0.0", "2.0.0");
        Assert.ThrowsAny<XunitException>(() => PluginRegistryAssertions.VerifyUpgrade(new(5, [old, promoted]), id, "1.0.0", "2.0.0"));
        Assert.ThrowsAny<XunitException>(() => PluginRegistryAssertions.VerifyUpgrade(new(5, [old with { Enabled = false }]), id, "1.0.0", "2.0.0"));
    }

    private sealed class ScriptedEnable(PluginState reply) : ICommandHandler<SetPluginEnabled, PluginState>
    {
        public Task<Outcome<PluginState>> HandleAsync(SetPluginEnabled request, CommandContext context, CancellationToken token) =>
            Task.FromResult<Outcome<PluginState>>(new Outcome<PluginState>.Success(reply));
    }
}
