using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves contribution effects with an independent expected playback fixture.</summary>
/// <remarks>2026-11-25: Gate FR-PLUGINS-001#ac-visualize and TEST-PLUGINS-001.</remarks>
public sealed class PluginActionMockTests
{
    [Fact]
    public void RejectsOtherSessionsAndMissingHostEffects()
    {
        var track = new QueueEntry(Guid.NewGuid(), Guid.NewGuid());
        var playing = new PlaybackState(Guid.NewGuid(), 3, new(2, [track], false), PlaybackCapabilities.Transport, TransportStatus.Playing,
            ActiveTrackId: track.TrackId, ActiveQueueEntryId: track.EntryId);
        var paused = playing with { Status = TransportStatus.Paused, Revision = 4 };
        PluginActionAssertions.Verify(playing, paused, "Paused by approved action");
        Assert.ThrowsAny<XunitException>(() => PluginActionAssertions.Verify(playing, playing, "Paused by approved action"));
        Assert.ThrowsAny<XunitException>(() => PluginActionAssertions.Verify(playing, paused with { SessionId = Guid.NewGuid() }, "Paused by approved action"));
        Assert.ThrowsAny<XunitException>(() => PluginActionAssertions.Verify(playing, paused with { Queue = paused.Queue with { Entries = [] } },
            "Paused by approved action"));
        Assert.ThrowsAny<XunitException>(() => PluginActionAssertions.Verify(playing, paused, "Changed result"));
    }
}
