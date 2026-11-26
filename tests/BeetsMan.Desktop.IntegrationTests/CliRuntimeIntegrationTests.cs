using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Playback;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs the actual CLI output against a controlled child process in both invocation modes.</summary>
/// <remarks>2026-11-25: Bind TEST-CLI-002 and FR-CLI-001#ac-switch for exact arguments, status, and process failures.</remarks>
public sealed class CliRuntimeIntegrationTests
{
    private readonly CancellationToken token = TestContext.Current.CancellationToken;
    private static readonly string Media = Path.Combine(Path.GetTempPath(), "track with spaces ; $(literal)'s file.mp3");
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CliPlaybackFixture.ps1");

    /// <summary>Verifies original paths, worker state, and seek position using the real playback port.</summary>
    [Theory]
    [InlineData(CliInvocationMode.OneShot)]
    [InlineData(CliInvocationMode.Session)]
    public async Task ReceivesStructuredTransportWithoutChangingTheMediaArgument(CliInvocationMode mode)
    {
        var track = Guid.NewGuid();
        using var output = Output(mode, track);
        Assert.Equal(PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, output.Capabilities);
        CliRuntimeAssertions.Verify(await Play(output, track), track, TransportStatus.Playing, 750);
        CliRuntimeAssertions.Verify(await output.SeekAsync(DeckId.A, track, TimeSpan.FromMilliseconds(2200), token),
            track, TransportStatus.Playing, 2200);
        CliRuntimeAssertions.Verify(await output.ReadAsync(DeckId.A, track, token), track, TransportStatus.Playing, 2200);
        CliRuntimeAssertions.Verify(await output.ChangeAsync(DeckId.A, track, null, TransportStatus.Paused, token),
            track, TransportStatus.Paused, 2200);
        CliRuntimeAssertions.Verify(await output.ChangeAsync(DeckId.A, track, null, TransportStatus.Stopped, token),
            track, TransportStatus.Stopped, 0);
    }

    /// <summary>Verifies process failure classification and recovery without replaying the failed action.</summary>
    [Theory]
    [InlineData(CliInvocationMode.OneShot, "exit", "cli-process-exited")]
    [InlineData(CliInvocationMode.Session, "exit", "cli-process-exited")]
    [InlineData(CliInvocationMode.OneShot, "timeout", "cli-process-timeout")]
    [InlineData(CliInvocationMode.Session, "timeout", "cli-process-timeout")]
    [InlineData(CliInvocationMode.OneShot, "malformed", "invalid-cli-reply")]
    [InlineData(CliInvocationMode.Session, "malformed", "invalid-cli-reply")]
    [InlineData(CliInvocationMode.OneShot, "oversize", "invalid-cli-reply")]
    [InlineData(CliInvocationMode.Session, "oversize", "invalid-cli-reply")]
    [InlineData(CliInvocationMode.Session, "wrongId", "invalid-cli-reply")]
    public async Task ClassifiesFailuresAndRequiresExplicitPlayForAReplacementWorker(CliInvocationMode mode, string delay, string code)
    {
        var track = Guid.NewGuid();
        using var output = Output(mode, track, delay);
        CliRuntimeAssertions.Verify(await Play(output, track), track, TransportStatus.Playing, 750);
        CliRuntimeAssertions.VerifyFailure(await output.ChangeAsync(DeckId.A, track, null, TransportStatus.Paused, token), code);
        if (mode == CliInvocationMode.Session)
            CliRuntimeAssertions.VerifyFailure(await output.ReadAsync(DeckId.A, track, token), "cli-process-exited");
        CliRuntimeAssertions.Verify(await output.ChangeAsync(DeckId.A, track, null, TransportStatus.Playing, token),
            track, TransportStatus.Playing, 750);
    }

    /// <summary>Distinguishes user cancellation from a profile timeout.</summary>
    [Fact]
    public async Task CancellationReapsTheSessionBeforeTheNextExplicitPlay()
    {
        var track = Guid.NewGuid();
        using var output = Output(CliInvocationMode.Session, track, "timeout");
        CliRuntimeAssertions.Verify(await Play(output, track), track, TransportStatus.Playing, 750);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
        Assert.IsType<Outcome<DeckState>.Cancelled>(await output.ChangeAsync(DeckId.A, track, null, TransportStatus.Paused, cancel.Token));
        CliRuntimeAssertions.VerifyFailure(await output.ReadAsync(DeckId.A, track, token), "cli-process-exited");
        CliRuntimeAssertions.Verify(await Play(output, track), track, TransportStatus.Playing, 750);
    }

    private Task<Outcome<DeckState>> Play(CliPlaybackOutput output, Guid track) =>
        output.ChangeAsync(DeckId.A, track, TimeSpan.FromMilliseconds(750), TransportStatus.Playing, token);

    private static CliPlaybackOutput Output(CliInvocationMode mode, Guid track, string pause = "pause")
    {
        var profile = FixtureProfile(mode, Media, pause);
        return new CliPlaybackOutput(profile, (id, _) => Task.FromResult<Outcome<(TrackAvailability, string?)>>(
            new Outcome<(TrackAvailability, string?)>.Success(id == track ? (TrackAvailability.Downloaded, Media) : (TrackAvailability.Unknown, null))));
    }

    /// <summary>Supplies the same controlled worker to transport and composed strategy tests.</summary>
    /// <remarks>2026-11-26: Give the Windows PowerShell fixture time to start its replacement worker while preserving the deliberate timeout for TEST-CLI-002 and FR-CLI-001#ac-switch.</remarks>
    internal static CliProfileDraft FixtureProfile(CliInvocationMode mode, string media, string pause = "pause")
    {
        CliActionMapping Map(CliPlaybackAction action, string kind) => mode == CliInvocationMode.Session
            ? new(action, [], kind) : new(action, [kind, CliArgumentToken.MediaPath, CliArgumentToken.PositionMilliseconds]);
        ImmutableArray<CliActionMapping> actions =
        [
            Map(CliPlaybackAction.Play, "play"), Map(CliPlaybackAction.Pause, pause),
            Map(CliPlaybackAction.Stop, "stop"), Map(CliPlaybackAction.Seek, "seek")
        ];
        if (mode == CliInvocationMode.Session)
            actions = actions.Add(Map(CliPlaybackAction.Status, "status"));
        var state = new Dictionary<string, TransportStatus>
        {
            ["running"] = TransportStatus.Playing, ["waiting"] = TransportStatus.Paused, ["idle"] = TransportStatus.Stopped
        }.ToImmutableDictionary(StringComparer.Ordinal);
        return new CliProfileDraft(Guid.NewGuid(), 1, 1, mode, "pwsh",
            ["-NoProfile", "-NonInteractive", "-File", Fixture, mode == CliInvocationMode.Session ? "session" : "oneShot", media],
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek | PlaybackCapabilities.Equalizer, TimeSpan.FromSeconds(8),
            actions, new("playerState", state, "where", "length"));
    }
}
