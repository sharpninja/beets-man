using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies every effective theme and its source throughout the STB07 Appearance workflow.</summary>
/// <remarks>2026-11-25: Pin the approved Appearance sequence for FR-SETTINGS-001#ac-precedence and TEST-SETTINGS-001.</remarks>
internal static class AppearancePreferenceAssertions
{
    internal sealed record Theme(string Value, string Source);
    internal sealed record Observation(Theme Initial, string Draft, Theme WhileDrafting, Theme Applied, Theme Inherited,
        string SharedWhileOverridden, bool DraftClearedAfterApply, bool DraftWroteNothing, string InvalidPreference);

    internal static void Verify(Observation actual)
    {
        var shared = new Theme("system", "HostDefault");
        Assert.Equal(shared, actual.Initial);
        Assert.Equal("dark", actual.Draft);
        Assert.Equal(shared, actual.WhileDrafting);
        Assert.Equal(new Theme("dark", "LocalOverride"), actual.Applied);
        Assert.Equal("system", actual.SharedWhileOverridden);
        Assert.Equal(shared, actual.Inherited);
        Assert.True(actual.DraftClearedAfterApply);
        Assert.True(actual.DraftWroteNothing);
        Assert.Equal("invalid-playback-preference", actual.InvalidPreference);
    }
}
