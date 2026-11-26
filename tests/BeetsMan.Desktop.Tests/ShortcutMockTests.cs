using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the shortcut acceptance assertions reject incorrect precedence, persistence, and system outcomes.</summary>
/// <remarks>2026-11-25: Independent fixture for FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-001.</remarks>
public sealed class ShortcutMockTests
{
    [Fact]
    public void ConflictAndDenialLeaveTheBindingUsable()
    {
        var valid = new ShortcutAssertions.Observation("shortcut-gesture-in-use", "playback.play-pause", "shell.close-dialog",
            "Ctrl+Shift+F", "Ctrl+Shift+F", false, "system-playback-shortcut-denied", 5, 5, "Space");
        ShortcutAssertions.Verify(valid);
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { ConflictCode = "wrong-conflict" }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { WinningAction = "playback.toggle-auto-fade" }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { ModalAction = "library.search" }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { ReopenedGesture = "" }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { SystemEnabled = true }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { DenialCode = "" }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { RevisionAfterDenial = 6 }));
        Assert.ThrowsAny<Exception>(() => ShortcutAssertions.Verify(valid with { OriginalLocalGesture = "" }));
    }
}
