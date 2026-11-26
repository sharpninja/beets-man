using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercise an independent stopped-engine handoff fixture before implementation.</summary>
/// <remarks>2026-11-25: Prove the switch assertions for FR-CLI-001#ac-switch and TEST-CLI-001.</remarks>
public sealed class PlaybackSwitchMockTests
{
    /// <summary>Rejects false availability, dropped selection and an implicit start after a native failure.</summary>
    /// <remarks>2026-11-26: Prove fallback observations for FR-PLAYER-001#ac-transport and FR-CLI-001#ac-switch.</remarks>
    [Fact]
    public void MissingNativeOutputAssertionsRejectLostQueueAndImplicitFallback()
    {
        const string unavailable = "audio-output-unavailable";
        PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, true, true, true, true, 1);
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable("success", true, true, true, true, true, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, false, true, true, true, true, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, false, true, true, true, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, true, false, true, true, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, true, true, false, true, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, true, true, true, false, 1));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyUnavailable(unavailable, true, true, true, true, true, 2));
    }

    [Fact]
    public void DistinguishesExternalShutdownFromThePersistentRestartCheckpoint()
    {
        PlaybackSwitchAssertions.VerifyLifecycle(false, true, true);
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyLifecycle(true, true, true));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyLifecycle(false, false, true));
        Assert.ThrowsAny<XunitException>(() => PlaybackSwitchAssertions.VerifyLifecycle(false, true, false));
    }

    [Fact]
    public void DistinguishesRetainedSelectionFromAnImplicitPlaybackStart()
    {
        var entry = new QueueEntry(Guid.NewGuid(), Guid.NewGuid());
        var earlier = new QueueEntry(Guid.NewGuid(), entry.TrackId);
        var source = new PlaybackState(Guid.NewGuid(), 7, new(2, [earlier, entry], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek,
            TransportStatus.Playing, ActiveTrackId: entry.TrackId, ActiveQueueEntryId: entry.EntryId);
        var handoff = source with { Revision = 8, Status = TransportStatus.Stopped, Capabilities = PlaybackCapabilities.Transport, Decks = [] };
        void Verify(PlaybackState result, string[] calls, int starts = 0) =>
            PlaybackSwitchAssertions.Verify(source, result, PlaybackCapabilities.Transport, calls, starts);
        Verify(handoff, ["Play", "Stop"]);
        Assert.ThrowsAny<XunitException>(() => Verify(handoff, ["Play"]));
        Assert.ThrowsAny<XunitException>(() => Verify(handoff, ["Play", "Stop"], 1));
        Assert.ThrowsAny<XunitException>(() => Verify(handoff with { Status = TransportStatus.Playing }, ["Play", "Stop"]));
        Assert.ThrowsAny<XunitException>(() => Verify(handoff with { ActiveQueueEntryId = earlier.EntryId }, ["Play", "Stop"]));
        Assert.ThrowsAny<XunitException>(() => Verify(handoff with { Queue = source.Queue with { Entries = [entry, earlier] } }, ["Play", "Stop"]));
        Assert.ThrowsAny<XunitException>(() => Verify(handoff with { Capabilities = source.Capabilities }, ["Play", "Stop"]));
    }
}
