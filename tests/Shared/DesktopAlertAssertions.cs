using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks desktop alert delivery against fresh local and inherited settings.</summary>
/// <remarks>2026-11-25: Cover STB07 and FR-SETTINGS-001#ac-precedence and ac-availability.</remarks>
internal static class DesktopAlertAssertions
{
    internal sealed record Observation(string[] Delivered, bool PlaybackOverrideSuppressed, bool PeerOverrideSuppressed,
        bool HistoricalAndRepeatedSharesIgnored);

    internal static void Verify(Observation actual)
    {
        Assert.Equal(["PeerNeedsAttention:fixture-share-failed", "PlaybackError:fixture-audio-after"], actual.Delivered);
        Assert.True(actual.PlaybackOverrideSuppressed);
        Assert.True(actual.PeerOverrideSuppressed);
        Assert.True(actual.HistoricalAndRepeatedSharesIgnored);
    }
}
