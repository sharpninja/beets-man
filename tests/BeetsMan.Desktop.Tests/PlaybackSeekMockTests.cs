using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;
using static BeetsMan.Desktop.Tests.PlaybackSeekAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the seek receipt assertions with correct and intentionally wrong fake results.</summary>
/// <remarks>2026-11-24: Exercise TEST-PLAYER-001 for FR-PLAYER-001#ac-transport before adding session handlers.</remarks>
public sealed class PlaybackSeekMockTests
{
    [Fact]
    public void AssertionsRejectSeekingAReplacementTrackOrOmittingTheBackendCall()
    {
        var state = new PlaybackState(Session, 2, new(1, [], false), PlaybackCapabilities.Seek,
            TransportStatus.Playing, Track, Target, Duration);
        Outcome<PlaybackState> result = new Outcome<PlaybackState>.Success(state);
        VerifySuccessfulSeek(result, [(DeckId.A, Track, Target)]);
        VerifyStaleSeek(new Outcome<PlaybackState>.Conflict("seek-track-changed", 2), 0);

        Assert.ThrowsAny<XunitException>(() => VerifySuccessfulSeek(result, []));
        Assert.ThrowsAny<XunitException>(() => VerifySuccessfulSeek(result, [(DeckId.A, Replacement, Target)]));
        Assert.ThrowsAny<XunitException>(() => VerifySuccessfulSeek(new Outcome<PlaybackState>.Success(state with { Position = TimeSpan.Zero }),
            [(DeckId.A, Track, Target)]));
        Assert.ThrowsAny<XunitException>(() => VerifyStaleSeek(new Outcome<PlaybackState>.Conflict("seek-track-changed", 2), 1));
    }
}
