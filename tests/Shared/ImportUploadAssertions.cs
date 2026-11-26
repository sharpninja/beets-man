using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Observes private file staging and acceptance of a single guided import operation.</summary>
/// <remarks>2026-11-24: Verify the upload checkpoint for FR-BEETS-001#ac-import, FR-BEETS-001#ac-recover, and TEST-BEETS-001.</remarks>
internal sealed record ImportUploadObservation(int UnauthenticatedStatus, int ForeignLibraryStatus, int OtherOwnerStatus,
    string CorruptPartCode, string IncompleteStartCode, string ChangedManifestCode, string[] PrivateNames,
    string[] OriginalContents, string[] StagedContents, int JobsBeforeComplete, int JobsAfterRetry,
    Guid AcceptedJob, Guid RecoveredJob, string RecoveredPhase, long InitialCatalogRevision, long FinalCatalogRevision);

/// <summary>Defines the guided upload assertions used with independent fixtures and the real host.</summary>
internal static class ImportUploadAssertions
{
    /// <summary>Requires private, intact copies and a single recoverable job after every copy is complete.</summary>
    public static void Verify(ImportUploadObservation observed)
    {
        Assert.Equal(401, observed.UnauthenticatedStatus);
        Assert.Equal(403, observed.ForeignLibraryStatus);
        Assert.Equal(404, observed.OtherOwnerStatus);
        Assert.Equal("import-copy-mismatch", observed.CorruptPartCode);
        Assert.Equal("import-upload-incomplete", observed.IncompleteStartCode);
        Assert.Equal("operation-intent-mismatch", observed.ChangedManifestCode);
        Assert.Equal(["Side A.flac", "Side B.flac"], observed.PrivateNames);
        Assert.Equal(["fixture audio A", "fixture audio B"], observed.OriginalContents);
        Assert.Equal(observed.OriginalContents, observed.StagedContents);
        Assert.Equal(0, observed.JobsBeforeComplete);
        Assert.Equal(1, observed.JobsAfterRetry);
        Assert.NotEqual(Guid.Empty, observed.AcceptedJob);
        Assert.Equal(observed.AcceptedJob, observed.RecoveredJob);
        Assert.Equal("Accepted", observed.RecoveredPhase);
        Assert.Equal(observed.InitialCatalogRevision, observed.FinalCatalogRevision);
    }
}
