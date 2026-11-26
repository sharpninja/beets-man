using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks successful and deliberately invalid system-key receipts before adapter implementation.</summary>
/// <remarks>2026-11-25: Independent FR-SETTINGS-001#ac-shortcuts controls.</remarks>
public sealed class SystemHotkeyMockTests
{
    [Fact]
    public void RegistrationOwnershipAndActivationHaveDiscriminatingAssertions()
    {
        var valid = new SystemHotkeyAssertions.Observation(true, true, 0xB3, 0x4000, true, true, ["Playing", "Paused", "Playing"], 0);
        SystemHotkeyAssertions.Verify(valid);
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { FirstVirtualKey = 0xB2 }));
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { FirstModifiers = 0 }));
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { PriorRetained = false }));
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { TemporaryReleased = false }));
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { TransportStates = ["Playing", "Paused", "Paused"] }));
        Assert.ThrowsAny<Exception>(() => SystemHotkeyAssertions.Verify(valid with { RemainingRegistrations = 1 }));
    }
}
