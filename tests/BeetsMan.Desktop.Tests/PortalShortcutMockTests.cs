using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the assertions detect registration and lifecycle errors.</summary>
/// <remarks>2026-11-25: Independent FR-SETTINGS-001#ac-shortcuts mock checks before portal implementation.</remarks>
public sealed class PortalShortcutMockTests
{
    /// <summary>Distinguishes the correct application, connection, and request order.</summary>
    /// <remarks>2026-11-25: Model the XDG 1.22 host identity requirement for FR-SETTINGS-001#ac-shortcuts.</remarks>
    [Fact]
    public void NewPortalAssociatesIdentityBeforeOpeningTheSamePeerSession()
    {
        PortalShortcutAssertions.VerifyHostIdentity("io.github.sharpninja.BeetsMan", ":1.8", ":1.8", true);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => PortalShortcutAssertions.VerifyHostIdentity("other.app", ":1.8", ":1.8", true));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => PortalShortcutAssertions.VerifyHostIdentity("io.github.sharpninja.BeetsMan", ":1.7", ":1.8", true));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => PortalShortcutAssertions.VerifyHostIdentity("io.github.sharpninja.BeetsMan", ":1.8", ":1.8", false));
    }

    [Fact]
    public void PortalDenialAndRevocationPreserveOnlyLiveAuthorization()
    {
        var valid = new PortalShortcutAssertions.Observation("XF86AudioPlay", "Media Play/Pause",
            "system-playback-shortcut-denied", true, true, ["Playing", "Paused"], false);
        PortalShortcutAssertions.Verify(valid);
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { PreferredTrigger = "Space" }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { PortalTrigger = null }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { DenialCode = "timeout" }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { PreviousSessionRetained = false }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { RejectedSessionClosed = false }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { TransportStates = ["Playing", "Paused", "Playing"] }));
        Assert.ThrowsAny<Exception>(() => PortalShortcutAssertions.Verify(valid with { EnabledAfterPortalClose = true }));
    }
}
