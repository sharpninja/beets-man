using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks correct and incorrect Appearance outcomes independently of production handlers.</summary>
/// <remarks>2026-11-25: Prove the STB07 draft, apply, inherit, and validation assertions for TEST-SETTINGS-001.</remarks>
/// <remarks>2026-11-26: Reject lost inheritance and wrong override outcomes for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed class AppearancePreferenceMockTests
{
    [Fact]
    public void ThemeAssertionsDiscriminateWrongValuesSourcesAndWrites()
    {
        var shared = new AppearancePreferenceAssertions.Theme("system", "HostDefault");
        var local = new AppearancePreferenceAssertions.Theme("dark", "LocalOverride");
        var valid = new AppearancePreferenceAssertions.Observation(shared, "dark", shared, local, shared,
            SharedWhileOverridden: "system", DraftClearedAfterApply: true, DraftWroteNothing: true,
            InvalidPreference: "invalid-playback-preference");

        AppearancePreferenceAssertions.Verify(valid);
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { Initial = local }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { Draft = "system" }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { WhileDrafting = local }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { Applied = shared }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { Inherited = local }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { SharedWhileOverridden = "dark" }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { DraftClearedAfterApply = false }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { DraftWroteNothing = false }));
        Assert.ThrowsAny<Exception>(() => AppearancePreferenceAssertions.Verify(valid with { InvalidPreference = "" }));
    }
}
