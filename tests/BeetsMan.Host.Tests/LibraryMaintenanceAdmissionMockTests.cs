using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the planned path and approval assertions against independent boundary fixtures.</summary>
/// <remarks>2026-11-24: Establish TEST-BEETS-001 and the STB18 mock gate.</remarks>
/// <remarks>2026-11-26: Prove separate catalog and audio removal decisions and organization review for FR-BEETS-001#ac-edit.</remarks>
public sealed class LibraryMaintenanceAdmissionMockTests
{
    [Fact]
    public void RejectsChangedPathsAndUnapprovedAudioRemoval()
    {
        var tracks = new[] { Guid.NewGuid() };
        var job = Guid.NewGuid();
        var valid = new LibraryMaintenanceAdmissionObservation(["Inbox/Waiting.flac -> Lena Vale/Northern Lines/01 Harbor Lights.flac"],
            tracks, tracks, [], ["Inbox/Waiting.flac"], "stale-library-maintenance", "audio-deletion-approval-required", "maintenance-plan-mode-mismatch",
            404, job, Guid.NewGuid(), Guid.NewGuid(), job, "Accepted", ["Organize", "RemoveAudio", "RemoveCatalog"], true, 42);
        LibraryMaintenanceAdmissionAssertions.Verify(valid);
        Reject(valid with { Moves = ["Inbox/Waiting.flac -> Somewhere/Else.flac"] });
        Reject(valid with { ReviewedTracks = [Guid.NewGuid()] });
        Reject(valid with { CatalogOnlyPaths = ["Inbox/Waiting.flac"] });
        Reject(valid with { DeletionPaths = [] });
        Reject(valid with { StaleCode = "accepted" });
        Reject(valid with { MissingApprovalCode = "accepted" });
        Reject(valid with { SwitchedModeCode = "accepted" });
        Reject(valid with { ForeignPlanStatus = 200 });
        Reject(valid with { OrganizeJob = Guid.Empty });
        Reject(valid with { CatalogOnlyJob = Guid.Empty });
        Reject(valid with { DeletionJob = Guid.Empty });
        Reject(valid with { ReplayedJob = Guid.NewGuid() });
        Reject(valid with { FirstPhase = "Published" });
        Reject(valid with { JournalKinds = ["Organize", "RemoveCatalog"] });
        Reject(valid with { FilesAndCatalogUnchanged = false });
        Reject(valid with { FeedRevision = 43 });
    }

    private static void Reject(LibraryMaintenanceAdmissionObservation wrong) =>
        Assert.ThrowsAny<XunitException>(() => LibraryMaintenanceAdmissionAssertions.Verify(wrong));
}
