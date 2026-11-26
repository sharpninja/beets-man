using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercises independently scripted playback results for CLI replies and failures.</summary>
/// <remarks>2026-11-25: Prove TEST-CLI-001/002 assertions before writing the process runtime.</remarks>
/// <remarks>2026-11-26: Reject wrong invocation outcomes and retry behavior for FR-CLI-001#ac-switch.</remarks>
public sealed class CliRuntimeMockTests
{
    [Fact]
    public async Task MapsPlaybackAndDistinguishesWorkerFailures()
    {
        var track = Guid.NewGuid();
        var started = new DeckState(DeckId.A, track, TimeSpan.FromMilliseconds(750), TimeSpan.FromSeconds(30),
            TransportStatus.Playing, true, 1);
        var pause = started with { Status = TransportStatus.Paused };
        string[] codes = ["cli-process-exited", "cli-process-timeout", "invalid-cli-reply"];
        var runtime = new ScriptedRuntime(new Queue<Outcome<DeckState>>([
            new Outcome<DeckState>.Success(started), new Outcome<DeckState>.Success(pause),
            .. codes.Select(code => new Outcome<DeckState>.DependencyFailure(code, true))
        ]));
        var token = TestContext.Current.CancellationToken;
        CliRuntimeAssertions.Verify(await runtime.ChangeAsync(DeckId.A, track, TimeSpan.FromMilliseconds(750), TransportStatus.Playing, token),
            track, TransportStatus.Playing, 750);
        CliRuntimeAssertions.Verify(await runtime.ChangeAsync(DeckId.A, track, null, TransportStatus.Paused, token),
            track, TransportStatus.Paused, 750);
        foreach (var code in codes)
        {
            var failure = await runtime.ChangeAsync(DeckId.A, track, null, TransportStatus.Paused, token);
            CliRuntimeAssertions.VerifyFailure(failure, code);
            Assert.ThrowsAny<XunitException>(() => CliRuntimeAssertions.VerifyFailure(failure, "different-worker-failure"));
        }
        Assert.ThrowsAny<XunitException>(() => CliRuntimeAssertions.Verify(new Outcome<DeckState>.Success(started), track, TransportStatus.Paused, 750));
        Assert.ThrowsAny<XunitException>(() => CliRuntimeAssertions.Verify(new Outcome<DeckState>.Success(started), track, TransportStatus.Playing, 751));
        Assert.ThrowsAny<XunitException>(() => CliRuntimeAssertions.Verify(new Outcome<DeckState>.Success(started), Guid.NewGuid(), TransportStatus.Playing, 750));
    }

    private sealed class ScriptedRuntime(Queue<Outcome<DeckState>> results) : IPlaybackTransportTarget
    {
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token) =>
            Task.FromResult(results.Dequeue());
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid track, CancellationToken token) => Task.FromResult(results.Dequeue());
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken token) => Task.FromResult(results.Dequeue());
    }
}
