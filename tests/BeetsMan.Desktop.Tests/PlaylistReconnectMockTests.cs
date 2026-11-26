using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Host.Contracts.V1;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Validates the reconnect contract against an independent scripted attempt history.</summary>
/// <remarks>2026-11-25: Verify discriminating assertions before binding FR-RECORDS-001#ac-offline and FR-LIBRARY-002#ac-batch-local.</remarks>
public sealed class PlaylistReconnectMockTests
{
    /// <summary>Rejects a transport observation from after the queued save was released.</summary>
    /// <remarks>2026-11-25: Exercise the independent mock gate for FR-PLAYER-001#ac-transport and ac-queue-autosave.</remarks>
    [Fact]
    public void DetectsPlaybackThatWaitsForTheRecoveredWrite()
    {
        var track = Guid.NewGuid();
        void Verify(TransportStatus transport, Guid? activeTrack, QueueSaveStatus save, bool responseHeld) =>
            PlaylistReconnectAssertions.VerifyTransportDuringSave(transport, activeTrack, save, responseHeld, track);

        Verify(TransportStatus.Playing, track, QueueSaveStatus.Pending, true);
        Assert.ThrowsAny<XunitException>(() => Verify(TransportStatus.Playing, track, QueueSaveStatus.Pending, false));
        Assert.ThrowsAny<XunitException>(() => Verify(TransportStatus.Stopped, track, QueueSaveStatus.Pending, true));
        Assert.ThrowsAny<XunitException>(() => Verify(TransportStatus.Playing, Guid.NewGuid(), QueueSaveStatus.Pending, true));
        Assert.ThrowsAny<XunitException>(() => Verify(TransportStatus.Playing, track, QueueSaveStatus.Saved, true));
    }

    [Fact]
    public void DetectsDroppedOrChangedWritesAndLostConflictDrafts()
    {
        var owner = Guid.NewGuid();
        var a = new PlaylistEntryData(Guid.NewGuid(), Guid.NewGuid());
        var b = new PlaylistEntryData(Guid.NewGuid(), Guid.NewGuid());
        var repeated = new PlaylistEntryData(Guid.NewGuid(), a.TrackId);
        var named = new PlaylistSaveRequest(owner, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
            "New playlist", [a, b, repeated]);
        var stale = new PlaylistSaveRequest(owner, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), Guid.NewGuid(), 3,
            "Reordered playlist", [b, a]);
        var queue = new PlaylistSaveRequest(owner, Guid.NewGuid(), new(PlaylistSaveSourceKind.Queue, Guid.NewGuid()), Guid.NewGuid(), 7,
            "Queue backing", [repeated, a, b]);
        PlaylistSaveRequest[] expected = [named, stale, queue];
        var observed = new PlaylistReconnectAssertions.Observation(0, 0, [stale, named, queue],
            PendingPlaylistSaveStatus.Accepted, QueueSaveStatus.Saved, PendingPlaylistSaveStatus.Conflicted, 4,
            [new(b.EntryId, b.TrackId), new(a.EntryId, a.TrackId)], 2);
        void Verify(PlaylistReconnectAssertions.Observation result) => PlaylistReconnectAssertions.Verify(result, expected, 4);
        Verify(observed);

        Assert.ThrowsAny<XunitException>(() => Verify(observed with { RequestsWhileBatchEnabled = 1 }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { BatchEntries = 1 }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { Replayed = [stale, named] }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { Replayed = [stale, named, queue with { Entries = [a, repeated, b] }] }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { Replayed = [stale with { ExpectedRevision = 4 }, named, queue] }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { Replayed = [stale, named with { OperationId = Guid.NewGuid() }, queue] }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { Stale = PendingPlaylistSaveStatus.Pending }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { LocalConflictEntries = [] }));
        Assert.ThrowsAny<XunitException>(() => Verify(observed with { AcceptedHostWrites = 3 }));
    }
}
