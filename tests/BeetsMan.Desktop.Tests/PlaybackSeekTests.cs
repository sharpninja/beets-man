using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using static BeetsMan.Desktop.Tests.PlaybackSeekAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercises captured-track seeks through the application dispatcher and a controlled deck.</summary>
/// <remarks>2026-11-24: Bind the TEST-PLAYER-001 seek case and keep stale gestures away from the audio backend.</remarks>
public sealed class PlaybackSeekTests
{
    private readonly RequestContext request = new(Guid.NewGuid(), null);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppliesTheBackendReceiptToTheSameSessionAndClearsPendingFade()
    {
        var target = new DeckTarget();
        var dispatcher = new PlaybackSessionHandlers(State(Track), DeckId.A, target).Register(new RequestDispatcher.Builder()).Build();
        var result = await dispatcher.SendAsync(new Seek(Session, Track, Target), new(request, Guid.NewGuid()), Token);
        VerifySuccessfulSeek(result, target.Seeks);
        var snapshot = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.QueryAsync(new GetPlaybackState(Session), request, Token)).Value;
        Assert.Equal(Target, snapshot.Position);
        Assert.False(snapshot.Output!.FadeActive);
    }

    [Fact]
    public async Task RejectsAnotherTrackAndOutOfRangePositionsBeforeCallingBackend()
    {
        var target = new DeckTarget();
        var dispatcher = new PlaybackSessionHandlers(State(Replacement), DeckId.A, target).Register(new RequestDispatcher.Builder()).Build();
        var result = await dispatcher.SendAsync(new Seek(Session, Track, Target), new(request, Guid.NewGuid()), Token);
        VerifyStaleSeek(result, target.Seeks.Count);
        Assert.IsType<Outcome<PlaybackState>.Invalid>(await dispatcher.SendAsync(new Seek(Session, Replacement, Duration + Target),
            new(request, Guid.NewGuid()), Token));
        Assert.Empty(target.Seeks);
    }

    [Fact]
    public async Task IndependentDeckSeekPreservesPrimaryPosition()
    {
        var target = new DeckTarget();
        var initial = State(Track);
        initial = initial with { Decks = initial.Decks.Add(new(DeckId.B, Replacement, TimeSpan.Zero, Duration, TransportStatus.Paused, true, 1)) };
        var dispatcher = new PlaybackSessionHandlers(initial, DeckId.A, target).Register(new RequestDispatcher.Builder()).Build();
        var result = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(new Seek(Session, Replacement, Target, DeckId.B),
            new(request, Guid.NewGuid()), Token)).Value;
        Assert.Equal(Track, result.ActiveTrackId);
        Assert.Equal(initial.Position, result.Position);
        Assert.Equal(Target, result.Decks.Single(deck => deck.DeckId == DeckId.B).Position);
        Assert.Equal((DeckId.B, Replacement, Target), Assert.Single(target.Seeks));
    }

    private static PlaybackState State(Guid active) => new(Session, 1, new(1, [], false), PlaybackCapabilities.Seek, TransportStatus.Playing,
        active, TimeSpan.Zero, Duration, [new(DeckId.A, active, TimeSpan.Zero, Duration, TransportStatus.Playing, true, 1)],
        new(1, 0, true, true, [], true));

    private sealed class DeckTarget : IPlaybackSeekTarget
    {
        public List<(DeckId, Guid, TimeSpan)> Seeks { get; } = [];

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken cancellationToken)
        {
            Seeks.Add((deck, track, position));
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(new(deck, track, position, Duration,
                deck == DeckId.A ? TransportStatus.Playing : TransportStatus.Paused, true, 1)));
        }
    }
}
