using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies owned Win32 registrations and exact playback activation sequence.</summary>
/// <remarks>2026-11-25: Independent FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002 controls.</remarks>
internal static class SystemHotkeyAssertions
{
    internal sealed record Observation(bool Applied, bool ReplacementDenied, uint FirstVirtualKey, uint FirstModifiers,
        bool PriorRetained, bool TemporaryReleased, string[] TransportStates, int RemainingRegistrations);

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.Applied);
        Assert.True(actual.ReplacementDenied);
        Assert.Equal(0xB3u, actual.FirstVirtualKey);
        Assert.Equal(0x4000u, actual.FirstModifiers);
        Assert.True(actual.PriorRetained);
        Assert.True(actual.TemporaryReleased);
        Assert.Equal(new[] { "Playing", "Paused", "Playing" }, actual.TransportStates);
        Assert.Equal(0, actual.RemainingRegistrations);
    }
}
