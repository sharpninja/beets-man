using BeetsMan.Desktop.Application.PresentationState;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies shell acceptance assertions against independent fixture snapshots.</summary>
/// <remarks>2026-11-24: Prove TEST-PLAYER-001 and TEST-FILESYNC-001 shell assertions for ac-modes and ac-arrival.</remarks>
public sealed class ShellStateMockTests
{
    [Fact]
    public void ModeAndArrivalProbesRejectLosingPlaybackOrDraftFocus()
    {
        var before = ShellStateAssertions.Start();
        var peer = Guid.NewGuid();
        var mock = new ShellReplyFixture(before);
        var full = mock.OpenFull();
        var restored = mock.Restore();
        var arrival = mock.Arrive(peer);
        ShellStateAssertions.VerifyModes(before, full, restored);
        ShellStateAssertions.VerifyArrival(before, arrival, peer);
        Assert.ThrowsAny<XunitException>(() => ShellStateAssertions.VerifyModes(before, full with { PlaybackSessionId = Guid.NewGuid() }, restored));
        Assert.ThrowsAny<XunitException>(() => ShellStateAssertions.VerifyArrival(before, arrival with { Overlays = [], FocusTarget = null }, peer));
    }

    private sealed class ShellReplyFixture(ShellState original)
    {
        public ShellState OpenFull() => original with { Revision = 2, Mode = PresentationMode.FullVisualization };
        public ShellState Restore() => original with { Revision = 3 };
        public ShellState Arrive(Guid peer) => original with
        {
            Revision = 4, Mode = PresentationMode.Normal, PriorWindowedMode = PresentationMode.Normal, Route = new(Workspace.Sync, peer)
        };
    }
}

internal static class ShellStateAssertions
{
    public static ShellState Start() => new(Guid.NewGuid(), 1, new(Workspace.Library), PresentationMode.Minimum,
        PresentationMode.Minimum, Guid.NewGuid(), [new(Guid.NewGuid(), Guid.NewGuid(), new OverlayIntent.PairMediaPeer(), "catalog")], "draft-input");

    public static void VerifyModes(ShellState original, ShellState full, ShellState restored)
    {
        Assert.Equal(PresentationMode.FullVisualization, full.Mode);
        Assert.Equal(PresentationMode.Minimum, full.PriorWindowedMode);
        Assert.Equal(PresentationMode.Minimum, restored.Mode);
        Assert.Equal(original.PlaybackSessionId, full.PlaybackSessionId);
        Assert.Equal(original.PlaybackSessionId, restored.PlaybackSessionId);
        Assert.Equal(original.Overlays, restored.Overlays);
    }

    public static void VerifyArrival(ShellState original, ShellState actual, Guid peer)
    {
        Assert.Equal(PresentationMode.Normal, actual.Mode);
        Assert.Equal(new WorkspaceRoute(Workspace.Sync, peer), actual.Route);
        Assert.Equal(original.PlaybackSessionId, actual.PlaybackSessionId);
        Assert.Equal(original.Overlays, actual.Overlays);
        Assert.Equal(original.FocusTarget, actual.FocusTarget);
    }
}
