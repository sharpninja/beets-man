using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the observed host effect of an approved plugin action.</summary>
/// <remarks>2026-11-25: Cover bounded CQRS contribution dispatch for FR-PLUGINS-001#ac-visualize.</remarks>
internal static class PluginActionAssertions
{
    internal static void Verify(PlaybackState before, PlaybackState after, string message)
    {
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.ActiveQueueEntryId, after.ActiveQueueEntryId);
        Assert.Equal(before.Queue, after.Queue);
        Assert.Equal(TransportStatus.Playing, before.Status);
        Assert.Equal(TransportStatus.Paused, after.Status);
        Assert.Equal("Paused by approved action", message);
    }
}
