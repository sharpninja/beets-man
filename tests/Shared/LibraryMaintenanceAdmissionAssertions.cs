using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Observes the read-only organization and mode-bound removal workflow.</summary>
/// <remarks>2026-11-24: Exercise STB18 and FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record LibraryMaintenanceAdmissionObservation(string[] Moves, Guid[] ReviewedTracks, Guid[] SelectedTracks, string[] CatalogOnlyPaths,
    string[] DeletionPaths, string StaleCode, string MissingApprovalCode, string SwitchedModeCode, int ForeignPlanStatus, Guid OrganizeJob,
    Guid CatalogOnlyJob, Guid DeletionJob, Guid ReplayedJob, string FirstPhase, string[] JournalKinds, bool FilesAndCatalogUnchanged, long FeedRevision);

/// <summary>Checks the exact preview, approved mode, and persisted replay without native effects.</summary>
internal static class LibraryMaintenanceAdmissionAssertions
{
    public static void Verify(LibraryMaintenanceAdmissionObservation result)
    {
        Assert.Equal(["Inbox/Waiting.flac -> Lena Vale/Northern Lines/01 Harbor Lights.flac"], result.Moves);
        Assert.Equal(result.SelectedTracks, result.ReviewedTracks);
        Assert.Empty(result.CatalogOnlyPaths);
        Assert.Equal(["Inbox/Waiting.flac"], result.DeletionPaths);
        Assert.Equal("stale-library-maintenance", result.StaleCode);
        Assert.Equal("audio-deletion-approval-required", result.MissingApprovalCode);
        Assert.Equal("maintenance-plan-mode-mismatch", result.SwitchedModeCode);
        Assert.Equal(404, result.ForeignPlanStatus);
        Assert.NotEqual(Guid.Empty, result.OrganizeJob);
        Assert.NotEqual(Guid.Empty, result.CatalogOnlyJob);
        Assert.NotEqual(Guid.Empty, result.DeletionJob);
        Assert.Equal(result.OrganizeJob, result.ReplayedJob);
        Assert.Equal("Accepted", result.FirstPhase);
        Assert.Equal(["Organize", "RemoveAudio", "RemoveCatalog"], result.JournalKinds);
        Assert.True(result.FilesAndCatalogUnchanged);
        Assert.Equal(42, result.FeedRevision);
    }
}
