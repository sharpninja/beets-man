using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.PresentationState;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Binds the proven shell state assertions to the registered application handlers.</summary>
/// <remarks>2026-11-24: Cover FR-PLAYER-001#ac-modes and FR-FILESYNC-001#ac-arrival with headless commands.</remarks>
public sealed class ShellStateTests
{
    [Fact]
    public async Task ModesAndLiveArrivalPreservePlaybackAndWindowOwnedDraft()
    {
        var initial = ShellStateAssertions.Start();
        var peer = Guid.NewGuid();
        var dispatcher = new ShellHandlers(initial, new Connection(peer, 9)).Register(new())
            .Build(typeof(ChangePresentation), typeof(ApplyAndroidPeerArrival), typeof(NavigateWorkspace));

        async Task<ShellState> Run(ICommand<ShellState> request) => Assert.IsType<Outcome<ShellState>.Success>(
            await dispatcher.SendAsync(request, new(new(Guid.NewGuid(), null), Guid.NewGuid()), CancellationToken.None)).Value;

        var full = await Run(new ChangePresentation(initial.WindowId, new(PresentationMode.FullVisualization)));
        var restored = await Run(new ChangePresentation(initial.WindowId, ModeSelection.RestoreWindowed));
        ShellStateAssertions.VerifyModes(initial, full, restored);
        Assert.Equal(restored, await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 8)));
        Assert.Equal(restored, await Run(new ApplyAndroidPeerArrival(initial.WindowId, Guid.NewGuid(), 9)));
        var arrival = await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 9));
        ShellStateAssertions.VerifyArrival(initial, arrival, peer);
        var movedAway = await Run(new NavigateWorkspace(initial.WindowId, new(Workspace.Library)));
        Assert.Equal(movedAway, await Run(new ApplyAndroidPeerArrival(initial.WindowId, peer, 9)));
    }

    [Fact]
    public async Task CloseTargetsOnlyTheCurrentOverlayAndRestoresItsFocus()
    {
        var initial = ShellStateAssertions.Start();
        var dispatcher = new ShellHandlers(initial, new Connection(Guid.NewGuid(), 0)).Register(new()).Build(typeof(CloseOverlay));
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

    private sealed class Connection(Guid peer, long generation) : IAndroidConnectionRegistry
    {
        public bool IsLiveRegisteredConnection(Guid peerId, long connectionGeneration) => peerId == peer && connectionGeneration == generation;
    }
}
