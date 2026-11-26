using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks normalized CLI replies and process failures at the playback port.</summary>
/// <remarks>2026-11-25: Cover FR-CLI-001#ac-switch and TEST-CLI-001/002.</remarks>
internal static class CliRuntimeAssertions
{
    internal static void Verify(Outcome<DeckState> result, Guid track, TransportStatus status, long positionMilliseconds)
    {
        var deck = Assert.IsType<Outcome<DeckState>.Success>(result).Value;
        Assert.Equal(DeckId.A, deck.DeckId);
        Assert.Equal(track, deck.TrackId);
        Assert.Equal(status, deck.Status);
        Assert.True(deck.Ready);
        Assert.Equal(TimeSpan.FromMilliseconds(positionMilliseconds), deck.Position);
        Assert.Equal(TimeSpan.FromSeconds(30), deck.Duration);
    }

    internal static void VerifyFailure(Outcome<DeckState> result, string code)
    {
        Assert.Equal(code, Assert.IsType<Outcome<DeckState>.DependencyFailure>(result).Code);
    }
}
