using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Observes the track revision, authorized tag read and single durable edit admission.</summary>
/// <remarks>2026-11-24: Cover SCR39 and FR-BEETS-001#ac-edit and ac-recover before binding the host.</remarks>
internal sealed record TrackTagAdmissionObservation(Guid TrackId, Guid ReadTrackId, long ReadRevision, string[] OriginalTags,
    int ForeignRead, int ForeignEdit, string StaleCode, long StaleActualRevision, string UnsupportedFieldCode, string ChangedReplayCode,
    string PendingCode, Guid JobId, Guid ReplayJobId, Guid OperationId, Guid RetryOperationId, string JobPhase,
    int AcceptedTrackJobs, long CatalogRevision, string[] JournaledChanges, bool DraftSurvivedUncertainReply);

/// <summary>Checks that a stale or foreign edit cannot reach the durable beets queue.</summary>
internal static class TrackTagAdmissionAssertions
{
    public static void Verify(TrackTagAdmissionObservation observed)
    {
        Assert.NotEqual(Guid.Empty, observed.TrackId);
        Assert.Equal(observed.TrackId, observed.ReadTrackId);
        Assert.Equal(184, observed.ReadRevision);
        Assert.Equal(["Northern Lines", "Lena Vale", "Lena Vale", "1", "Ambient", "Harbor Lights", "1", "2023"], observed.OriginalTags);
        Assert.Equal(404, observed.ForeignRead);
        Assert.Equal(404, observed.ForeignEdit);
        Assert.Equal("stale-track-tags", observed.StaleCode);
        Assert.Equal(184, observed.StaleActualRevision);
        Assert.Equal("invalid-track-tag-edit", observed.UnsupportedFieldCode);
        Assert.Equal("operation-intent-mismatch", observed.ChangedReplayCode);
        Assert.Equal("track-tag-edit-pending", observed.PendingCode);
        Assert.NotEqual(Guid.Empty, observed.JobId);
        Assert.Equal(observed.JobId, observed.ReplayJobId);
        Assert.NotEqual(Guid.Empty, observed.OperationId);
        Assert.Equal(observed.OperationId, observed.RetryOperationId);
        Assert.Equal("Accepted", observed.JobPhase);
        Assert.Equal(1, observed.AcceptedTrackJobs);
        Assert.Equal(185, observed.CatalogRevision);
        Assert.Equal(["genre=Downtempo", "year=<clear>"], observed.JournaledChanges);
        Assert.True(observed.DraftSurvivedUncertainReply);
    }
}
