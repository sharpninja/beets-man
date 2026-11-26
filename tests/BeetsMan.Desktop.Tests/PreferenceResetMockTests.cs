using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks independent preview and confirmation evidence before implementing notification reset.</summary>
/// <remarks>2026-11-25: Deliberate incorrect observations for TEST-SETTINGS-001 and STB07.</remarks>
public sealed class PreferenceResetMockTests
{
    /// <summary>Proves the alert assertions discriminate wrong sources, duplicate delivery, and disabled values.</summary>
    /// <remarks>2026-11-25: Exercise independent controls for the STB07 flags under FR-SETTINGS-001#ac-precedence.</remarks>
    [Fact]
    public void AlertAssertionsRejectDisabledOrRepeatedDelivery()
    {
        var correct = new DesktopAlertAssertions.Observation(["PeerNeedsAttention:fixture-share-failed", "PlaybackError:fixture-audio-after"],
            true, true, true);
        DesktopAlertAssertions.Verify(correct);
        DesktopAlertAssertions.Observation[] wrong =
        [
            correct with { Delivered = ["PlaybackError:fixture-audio-before", .. correct.Delivered] },
            correct with { Delivered = ["PlaybackError:fixture-audio-after"] },
            correct with { PlaybackOverrideSuppressed = false },
            correct with { PeerOverrideSuppressed = false },
            correct with { HistoricalAndRepeatedSharesIgnored = false }
        ];
        Assert.All(wrong, result => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DesktopAlertAssertions.Verify(result)));
    }

    [Fact]
    public void PreviewAndCommitAssertionsDiscriminateUnreviewedEffects()
    {
        var valid = new PreferenceResetAssertions.Observation([
            new("notifications.syncCompleted", false, true, "HostDefault"),
            new("notifications.playbackErrors", false, true, "BuiltIn")
        ], "stale-local-preferences", "stale-shared-preferences", true, true, true, true, true);
        PreferenceResetAssertions.Verify(valid);
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { Preview = [valid.Preview[0]] }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { LocalConflict = "" }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { HostConflict = "" }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { PreservedBeforeApproval = false }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { InheritedSync = false }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { InheritedPlaybackErrors = false }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { UnrelatedSettingRetained = false }));
        Assert.ThrowsAny<Exception>(() => PreferenceResetAssertions.Verify(valid with { SharedValueRetained = false }));
    }
}
