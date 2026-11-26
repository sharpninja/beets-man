using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies registration conflict and release against user32 on a Windows desktop session.</summary>
/// <remarks>2026-11-25: Cover the native Win32 ownership part of TEST-SETTINGS-002 without sending input.</remarks>
/// <remarks>2026-11-26: Verify Windows shortcut registration conflicts and release for FR-SETTINGS-001#ac-shortcuts.</remarks>
[Trait("Platform", "Windows")]
public sealed class Win32NativeShortcutIntegrationTests
{
    [Fact]
    public async Task User32RejectsADuplicateUntilTheOriginalOwnerCloses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires the Windows user32 message queue.");
        var token = TestContext.Current.CancellationToken;
        await using var first = new Win32ShortcutPlatform(new Win32ShortcutApi());
        await using var second = new Win32ShortcutPlatform(new Win32ShortcutApi());
        var binding = new ShortcutBinding(ShortcutCatalog.PlayPause, ShortcutScopes.System, "Ctrl+Alt+Shift+F24");
        Assert.True(Of(await first.ApplyAsync([binding], token)));
        Assert.False(Of(await second.ApplyAsync([binding], token)));
        await first.DisposeAsync();
        Assert.True(Of(await second.ApplyAsync([binding], token)));
        Assert.True(Of(await second.ApplyAsync([], token)));

        static bool Of(Outcome<bool> result) => Assert.IsType<Outcome<bool>.Success>(result).Value;
    }
}
