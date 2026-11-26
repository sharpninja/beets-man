using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies portal registration, explicit denial and session-bound activation observations.</summary>
/// <remarks>2026-11-25: Independent test evidence for FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002.</remarks>
internal static class PortalShortcutAssertions
{
    /// <summary>Requires one application registration on the same peer before creating the shortcut session.</summary>
    /// <remarks>2026-11-25: Prove the modern host portal handshake for FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002.</remarks>
    internal static void VerifyHostIdentity(string registeredAppId, string registrationPeer, string sessionPeer, bool registeredBeforeSession)
    {
        Assert.Equal("io.github.sharpninja.BeetsMan", registeredAppId);
        Assert.Equal(sessionPeer, registrationPeer);
        Assert.True(registeredBeforeSession);
    }

    internal sealed record Observation(string PreferredTrigger, string? PortalTrigger, string DenialCode,
        bool PreviousSessionRetained, bool RejectedSessionClosed, string[] TransportStates, bool EnabledAfterPortalClose);

    internal static void Verify(Observation actual)
    {
        Assert.Equal("XF86AudioPlay", actual.PreferredTrigger);
        Assert.Equal("Media Play/Pause", actual.PortalTrigger);
        Assert.Equal("system-playback-shortcut-denied", actual.DenialCode);
        Assert.True(actual.PreviousSessionRetained);
        Assert.True(actual.RejectedSessionClosed);
        Assert.Equal(new[] { "Playing", "Paused" }, actual.TransportStates);
        Assert.False(actual.EnabledAfterPortalClose);
    }
}
