using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks captured-track seek behavior against the same transport observations.</summary>
/// <remarks>2026-11-24: Define independent TEST-PLAYER-001 seek assertions for FR-PLAYER-001#ac-transport.</remarks>
internal static class PlaybackSeekAssertions
{
    public static readonly Guid Session = Guid.Parse("81000000-0000-0000-0000-000000000001");
    public static readonly Guid Track = Guid.Parse("82000000-0000-0000-0000-000000000001");
    public static readonly Guid Replacement = Guid.Parse("82000000-0000-0000-0000-000000000002");
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Target = TimeSpan.FromSeconds(42);

    public static void VerifySuccessfulSeek(Outcome<PlaybackState> result, IReadOnlyList<(DeckId, Guid, TimeSpan)> seeks)
    {
        var state = Assert.IsType<Outcome<PlaybackState>.Success>(result).Value;
        Assert.Equal(Session, state.SessionId);
        Assert.Equal(Track, state.ActiveTrackId);
        Assert.Equal(Target, state.Position);
        Assert.Equal(TransportStatus.Playing, state.Status);
        Assert.Equal((DeckId.A, Track, Target), Assert.Single(seeks));
    }

    public static void VerifyStaleSeek(Outcome<PlaybackState> result, int backendCalls)
    {
        Assert.Equal("seek-track-changed", Assert.IsType<Outcome<PlaybackState>.Conflict>(result).Code);
        Assert.Equal(0, backendCalls);
    }
}
