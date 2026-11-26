using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies the retained selection and explicit Play boundary after a backend handoff.</summary>
/// <remarks>2026-11-25: Coverage for FR-CLI-001#ac-switch and TEST-CLI-001.</remarks>
internal static class PlaybackSwitchAssertions
{
    internal static void Verify(PlaybackState old, PlaybackState switched, PlaybackCapabilities expected, string[] priorCalls, int incomingStarts)
    {
        Assert.Equal(old.SessionId, switched.SessionId);
        Assert.True(switched.Revision > old.Revision);
        Assert.Equal(old.Queue, switched.Queue);
        Assert.Equal(old.ActiveQueueEntryId, switched.ActiveQueueEntryId);
        Assert.Equal(old.ActiveTrackId, switched.ActiveTrackId);
        Assert.Equal(TransportStatus.Stopped, switched.Status);
        Assert.Equal(expected, switched.Capabilities);
        Assert.Empty(switched.Decks);
        Assert.Equal(new[] { "Play", "Stop" }, priorCalls);
        Assert.Equal(0, incomingStarts);
    }

    /// <summary>Verifies that the native clock leaves CLI I/O to explicit commands and shutdown retains resume eligibility.</summary>
    /// <remarks>2026-11-25: Bind FR-CLI-001#ac-switch and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
    internal static void VerifyLifecycle(bool nativePollIssuedCliStatus, bool savedAsPlaying, bool stoppedExternal)
    {
        Assert.False(nativePollIssuedCliStatus);
        Assert.True(savedAsPlaying);
        Assert.True(stoppedExternal);
    }

    /// <summary>Checks that a missing native device leaves a reusable queue and explicit fallback.</summary>
    /// <remarks>2026-11-26: Verify typed native failure for FR-PLAYER-001#ac-transport and fallback for FR-CLI-001#ac-switch.</remarks>
    internal static void VerifyUnavailable(string failureCode, bool retryable, bool queueRetained, bool cliOffered,
        bool explicitStart, bool cliPlaying, int nativeAttempts)
    {
        Assert.Equal("audio-output-unavailable", failureCode);
        Assert.True(retryable);
        Assert.True(queueRetained);
        Assert.True(cliOffered);
        Assert.True(explicitStart);
        Assert.True(cliPlaying);
        Assert.Equal(1, nativeAttempts);
    }
}
