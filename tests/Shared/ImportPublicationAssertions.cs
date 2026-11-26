using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Records the effects of publishing verified album rows on the host catalog and notification feed.</summary>
/// <remarks>2026-11-24: Verify the reconciliation boundary for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal sealed record ImportPublicationObservation(long RevisionBefore, long RevisionAfterPartial, long PublishedRevision, long ReplayedRevision,
    string PartialCode, string WrongOperationCode, string ChangedReplayCode, string JobPhase, long JobRevision,
    Guid ExistingTrackId, Guid PublishedExistingTrackId, long[] PublishedNativeTrackIds, string[] RelativePaths,
    string[] ChangeKinds, Guid ExpectedOperationId, Guid PublishedOperationId, Guid ExpectedRecipientId, Guid[] DeliveryRecipients, int ChangeSets);

/// <summary>Defines shared checks for one all-or-nothing guided album publication and its replay.</summary>
internal static class ImportPublicationAssertions
{
    /// <summary>Checks the projection, feed, delivery and job result share a single committed revision.</summary>
    public static void Verify(ImportPublicationObservation publication)
    {
        Assert.Equal(41, publication.RevisionBefore);
        Assert.Equal(publication.RevisionBefore, publication.RevisionAfterPartial);
        Assert.Equal(42, publication.PublishedRevision);
        Assert.Equal(publication.PublishedRevision, publication.ReplayedRevision);
        Assert.Equal("import-reconciliation-incomplete", publication.PartialCode);
        Assert.Equal("import-publication-scope-mismatch", publication.WrongOperationCode);
        Assert.Equal("import-publication-intent-mismatch", publication.ChangedReplayCode);
        Assert.Equal("Published", publication.JobPhase);
        Assert.Equal(4, publication.JobRevision);
        Assert.NotEqual(Guid.Empty, publication.ExistingTrackId);
        Assert.Equal(publication.ExistingTrackId, publication.PublishedExistingTrackId);
        Assert.Equal([27L, 28L], publication.PublishedNativeTrackIds);
        Assert.Equal(["Fixture Band/Fixture Album/01 North.flac", "Fixture Band/Fixture Album/02 South.flac"], publication.RelativePaths);
        Assert.Equal(["Artist", "Album", "Track", "Track"], publication.ChangeKinds);
        Assert.NotEqual(Guid.Empty, publication.ExpectedOperationId);
        Assert.Equal(publication.ExpectedOperationId, publication.PublishedOperationId);
        Assert.NotEqual(Guid.Empty, publication.ExpectedRecipientId);
        Assert.Equal([publication.ExpectedRecipientId], publication.DeliveryRecipients);
        Assert.Equal(1, publication.ChangeSets);
    }
}
