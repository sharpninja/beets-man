using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Asserts the original write intents and the durable result of automatic reconnect.</summary>
/// <remarks>2026-11-25: Cover FR-RECORDS-001#ac-offline, FR-PLAYER-001#ac-queue-autosave and FR-LIBRARY-002#ac-batch-local.</remarks>
internal static class PlaylistReconnectAssertions
{
    internal sealed record Observation(int RequestsWhileBatchEnabled, int BatchEntries, PlaylistSaveRequest[] Replayed,
        PendingPlaylistSaveStatus Named, QueueSaveStatus Queue, PendingPlaylistSaveStatus Stale, long? ActualRevision,
        PlaylistEntry[] LocalConflictEntries, int AcceptedHostWrites);

    /// <summary>Checks that playback advances while a recovered queue write is still waiting for the host.</summary>
    /// <remarks>2026-11-25: Verify FR-PLAYER-001#ac-transport with pending FR-PLAYER-001#ac-queue-autosave.</remarks>
    internal static void VerifyTransportDuringSave(TransportStatus transport, Guid? activeTrack, QueueSaveStatus saveStatus,
        bool responseHeld, Guid expectedTrack)
    {
        Assert.True(responseHeld);
        Assert.Equal(TransportStatus.Playing, transport);
        Assert.Equal(expectedTrack, activeTrack);
        Assert.Equal(QueueSaveStatus.Pending, saveStatus);
    }

    internal static void Verify(Observation observed, PlaylistSaveRequest[] expected, long actualRevision)
    {
        Assert.Equal(0, observed.RequestsWhileBatchEnabled);
        Assert.Equal(0, observed.BatchEntries);
        Assert.Equal(expected.Length, observed.Replayed.Length);
        foreach (var saved in expected)
        {
            var replay = Assert.Single(observed.Replayed, body => body.OperationId == saved.OperationId);
            Assert.Equal(saved with { Entries = replay.Entries }, replay);
            Assert.Equal(saved.Entries.ToArray(), replay.Entries.ToArray());
        }
        Assert.Equal(PendingPlaylistSaveStatus.Accepted, observed.Named);
        Assert.Equal(QueueSaveStatus.Saved, observed.Queue);
        Assert.Equal(PendingPlaylistSaveStatus.Conflicted, observed.Stale);
        Assert.Equal(actualRevision, observed.ActualRevision);
        var stale = Assert.Single(expected, save => save.ExpectedRevision == actualRevision - 1 && save.Source.Kind == PlaylistSaveSourceKind.Draft);
        Assert.Equal(stale.Entries.Select(item => new PlaylistEntry(item.EntryId, item.TrackId)), observed.LocalConflictEntries);
        Assert.Equal(2, observed.AcceptedHostWrites);
    }
}
