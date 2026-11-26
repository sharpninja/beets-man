using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies revision-pinned notification resets against explicit local and inherited values.</summary>
/// <remarks>2026-11-25: Independent assertions for FR-SETTINGS-001#ac-precedence, ac-availability and STB07.</remarks>
internal static class PreferenceResetAssertions
{
    internal sealed record Row(string Key, bool Before, bool After, string Source);
    internal sealed record Observation(Row[] Preview, string LocalConflict, string HostConflict, bool PreservedBeforeApproval,
        bool InheritedSync, bool InheritedPlaybackErrors, bool UnrelatedSettingRetained, bool SharedValueRetained);

    internal static void Verify(Observation actual)
    {
        Assert.Equal(new[]
        {
            new Row("notifications.syncCompleted", false, true, "HostDefault"),
            new Row("notifications.playbackErrors", false, true, "BuiltIn")
        }, actual.Preview);
        Assert.Equal("stale-local-preferences", actual.LocalConflict);
        Assert.Equal("stale-shared-preferences", actual.HostConflict);
        Assert.True(actual.PreservedBeforeApproval);
        Assert.True(actual.InheritedSync);
        Assert.True(actual.InheritedPlaybackErrors);
        Assert.True(actual.UnrelatedSettingRetained);
        Assert.True(actual.SharedValueRetained);
    }
}
