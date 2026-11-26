using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks conflict identity, contextual precedence, and denial without a local edit.</summary>
/// <remarks>2026-11-25: Independent observations for FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-001.</remarks>
internal static class ShortcutAssertions
{
    internal sealed record Observation(string ConflictCode, string? WinningAction, string? ModalAction,
        string AssignedGesture, string ReopenedGesture, bool SystemEnabled, string DenialCode, long RevisionBeforeDenial,
        long RevisionAfterDenial, string OriginalLocalGesture);

    internal static void Verify(Observation actual)
    {
        Assert.Equal("shortcut-gesture-in-use", actual.ConflictCode);
        Assert.Equal("playback.play-pause", actual.WinningAction);
        Assert.Equal("shell.close-dialog", actual.ModalAction);
        Assert.Equal("Ctrl+Shift+F", actual.AssignedGesture);
        Assert.Equal(actual.AssignedGesture, actual.ReopenedGesture);
        Assert.False(actual.SystemEnabled);
        Assert.Equal("system-playback-shortcut-denied", actual.DenialCode);
        Assert.Equal(actual.RevisionBeforeDenial, actual.RevisionAfterDenial);
        Assert.Equal("Space", actual.OriginalLocalGesture);
    }
}
