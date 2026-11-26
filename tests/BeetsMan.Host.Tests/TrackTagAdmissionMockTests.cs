using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises independent observations for revision checks, tag drafts and operation replay.</summary>
/// <remarks>2026-11-24: Establish the isolated mock gate for the SCR39 request and result, FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class TrackTagAdmissionMockTests
{
    [Fact]
    public void RejectsStaleForeignOrDuplicateTagAdmissions()
    {
        var track = new Guid("ba6c57be-c3a4-488a-82f2-e252446ab682");
        var job = new Guid("86f517d0-4699-43e4-8740-bece5ee41091");
        var operation = new Guid("0b717b64-2c50-4e1b-827a-632c31bd0c71");
        var observed = new TrackTagAdmissionObservation(track, track, 184,
            ["Northern Lines", "Lena Vale", "Lena Vale", "1", "Ambient", "Harbor Lights", "1", "2023"], 404, 404,
            "stale-track-tags", 184, "invalid-track-tag-edit", "operation-intent-mismatch", "track-tag-edit-pending",
            job, job, operation, operation, "Accepted", 1, 185, ["genre=Downtempo", "year=<clear>"], true);
        TrackTagAdmissionAssertions.Verify(observed);
        Reject(observed with { ReadTrackId = Guid.NewGuid() });
        Reject(observed with { ReadRevision = 185 });
        Reject(observed with { OriginalTags = ["Harbor Lights"] });
        Reject(observed with { ForeignRead = 200 });
        Reject(observed with { ForeignEdit = 200 });
        Reject(observed with { StaleCode = "accepted" });
        Reject(observed with { StaleActualRevision = 185 });
        Reject(observed with { UnsupportedFieldCode = "accepted" });
        Reject(observed with { ChangedReplayCode = "accepted" });
        Reject(observed with { PendingCode = "accepted" });
        Reject(observed with { ReplayJobId = Guid.NewGuid() });
        Reject(observed with { RetryOperationId = Guid.NewGuid() });
        Reject(observed with { JobPhase = "Published" });
        Reject(observed with { AcceptedTrackJobs = 2 });
        Reject(observed with { CatalogRevision = 186 });
        Reject(observed with { JournaledChanges = ["genre=Downtempo"] });
        Reject(observed with { DraftSurvivedUncertainReply = false });
    }

    private static void Reject(TrackTagAdmissionObservation observed) =>
        Assert.ThrowsAny<XunitException>(() => TrackTagAdmissionAssertions.Verify(observed));
}
