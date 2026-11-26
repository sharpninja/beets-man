using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.PresentationState;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Binds the proven shell state assertions to the registered application handlers.</summary>
/// <remarks>2026-11-24: Cover FR-PLAYER-001#ac-modes and FR-FILESYNC-001#ac-arrival with headless commands.</remarks>
public sealed class ShellStateTests
{
    [Fact]
    public async Task CloseTargetsOnlyTheCurrentOverlayAndRestoresItsFocus()
    {
        var initial = ShellStateAssertions.Start();
        var dispatcher = new ShellHandlers(initial, null).Register(new()).Build(typeof(CloseOverlay));
        var context = new CommandContext(new(Guid.NewGuid(), null), Guid.NewGuid());
        var invalid = await dispatcher.SendAsync(new CloseOverlay(initial.WindowId, Guid.NewGuid(), OverlayCloseReason.Cancel), context, CancellationToken.None);
        Assert.Equal("overlay-no-longer-active", Assert.IsType<Outcome<ShellState>.Conflict>(invalid).Code);
        var accepted = await dispatcher.SendAsync(new CloseOverlay(initial.WindowId, initial.Overlays[0].OverlayId, OverlayCloseReason.Cancel), context,
            CancellationToken.None);
        var state = Assert.IsType<Outcome<ShellState>.Success>(accepted).Value;
        Assert.Empty(state.Overlays);
        Assert.Equal("catalog", state.FocusTarget);
        Assert.Equal(initial.PlaybackSessionId, state.PlaybackSessionId);
    }

}

/// <summary>Exercises all presentation modes through the shell CQRS handlers.</summary>
/// <remarks>2026-11-26: Bind the named TEST-PLAYER-001 scenario to FR-PLAYER-001#ac-modes.</remarks>
public sealed partial class PLAYERAcceptanceTests
{
    /// <summary>Restores the previous windowed mode and leaves the playback session and draft intact.</summary>
    [Theory]
    [InlineData(PresentationMode.Normal)]
    [InlineData(PresentationMode.Minimum)]
    [InlineData(PresentationMode.BoundedVisualization)]
    public async Task Modes_MatchesContract(PresentationMode windowed)
    {
        var initial = ShellStateAssertions.Start();
        var dispatcher = new ShellHandlers(initial, null).Register(new()).Build();
        var ready = await Run(new ChangePresentation(initial.WindowId, new(windowed)));
        var full = await Run(new ChangePresentation(initial.WindowId, new(PresentationMode.FullVisualization)));
        var restored = await Run(new ChangePresentation(initial.WindowId, ModeSelection.RestoreWindowed));
        ShellStateAssertions.VerifyModes(ready, full, restored);
        Assert.Equal(initial.PlaybackSessionId, restored.PlaybackSessionId);
        Assert.Equal(initial.FocusTarget, restored.FocusTarget);
        Assert.Equal(initial.Route, restored.Route);

        async Task<ShellState> Run(ICommand<ShellState> request) => Assert.IsType<Outcome<ShellState>.Success>(
            await dispatcher.SendAsync(request, new(new(Guid.NewGuid(), null), Guid.NewGuid()), TestContext.Current.CancellationToken)).Value;
    }
}

public sealed partial class FILESYNCAcceptanceTests
{
    /// <summary>Uses only a new registered Android connection to navigate without losing playback or draft focus.</summary>
    /// <remarks>2026-11-26: Bind the named TEST-FILESYNC-001 scenario to FR-FILESYNC-001#ac-arrival.</remarks>
    [Theory]
    [InlineData(PresentationMode.Normal)]
    [InlineData(PresentationMode.Minimum)]
    [InlineData(PresentationMode.BoundedVisualization)]
    [InlineData(PresentationMode.FullVisualization)]
    public async Task Arrival_MatchesContract(PresentationMode mode)
    {
        var initial = ShellStateAssertions.Start() with { Mode = mode };
        var peer = Guid.NewGuid();
        var dispatcher = new ShellHandlers(initial, new Connection(peer, 9)).Register(new()).Build();
        Assert.Equal(initial, await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 8)));
        Assert.Equal(initial, await Run(new ApplyAndroidPeerArrival(initial.WindowId, Guid.NewGuid(), 9)));
        var arrival = await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 9));
        ShellStateAssertions.VerifyArrival(initial, arrival, peer);
        var movedAway = await Run(new NavigateWorkspace(initial.WindowId, new(Workspace.Library)));
        Assert.Equal(movedAway, await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 9)));

        async Task<ShellState> Run(ICommand<ShellState> request) => Assert.IsType<Outcome<ShellState>.Success>(
            await dispatcher.SendAsync(request, new(new(Guid.NewGuid(), null), Guid.NewGuid()), TestContext.Current.CancellationToken)).Value;
    }

    private sealed class Connection(Guid peer, long generation) : IAndroidConnectionRegistry
    {
        public bool IsLiveRegisteredConnection(Guid peerId, long connectionGeneration) => peerId == peer && connectionGeneration == generation;
    }
}
