using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Captures the observable album review and approval boundary.</summary>
/// <remarks>2026-11-24: Cover the three STB08 review choices and replay for FR-BEETS-001#ac-import and ac-recover, TEST-BEETS-001.</remarks>
internal sealed record GuidedImportReviewObservation(Guid JobId, Guid ReviewItemId, Guid RestartedReviewItemId,
    string PhaseBeforeChoice, string[] SourceNames, string[] CandidateTitles, long ReviewRevision, long RestartedReviewRevision,
    int ForeignReadStatus, int ForeignChoiceStatus, string StaleChoiceCode, string UnknownMatchCode);

/// <summary>Captures a submitted album choice, its replay, and any external or catalog effects at the approval boundary.</summary>
internal sealed record GuidedImportChoiceObservation(Guid JobId, Guid ReceiptJobId, Guid ChoiceOperationId, Guid ReplayOperationId,
    long ChoiceRevision, long ReplayRevision, string Choice, string Phase, string? PersistedCandidate, int SavedIntents,
    string ChangedReplayCode, long CatalogRevisionBefore, long CatalogRevisionAfter, int CatalogDeliveries, int ExternalWrites);

/// <summary>Verifies the same review and approval behavior for independently scripted and production-bound jobs.</summary>
internal static class GuidedImportReviewAssertions
{
    /// <summary>Checks an album review survives restart and remains visible to the originating desktop.</summary>
    public static void VerifyReview(GuidedImportReviewObservation observed)
    {
        Assert.NotEqual(Guid.Empty, observed.JobId);
        Assert.NotEqual(Guid.Empty, observed.ReviewItemId);
        Assert.Equal(observed.ReviewItemId, observed.RestartedReviewItemId);
        Assert.Equal("ReviewRequired", observed.PhaseBeforeChoice);
        Assert.Equal(["North.flac", "South.flac", "Harbor.flac"], observed.SourceNames);
        Assert.Equal(["Fixture Album", "Alternate Album"], observed.CandidateTitles);
        Assert.Equal(2, observed.ReviewRevision);
        Assert.Equal(observed.ReviewRevision, observed.RestartedReviewRevision);
        Assert.Equal(404, observed.ForeignReadStatus);
        Assert.Equal(404, observed.ForeignChoiceStatus);
        Assert.Equal("stale-import-review", observed.StaleChoiceCode);
        Assert.Equal("unknown-import-match", observed.UnknownMatchCode);
    }

    /// <summary>Checks journal-before-effect ordering, terminal skip, single acceptance and stable replay.</summary>
    public static void VerifyChoice(GuidedImportChoiceObservation observed, string choice)
    {
        Assert.NotEqual(Guid.Empty, observed.JobId);
        Assert.Equal(observed.JobId, observed.ReceiptJobId);
        Assert.NotEqual(Guid.Empty, observed.ChoiceOperationId);
        Assert.Equal(observed.ChoiceOperationId, observed.ReplayOperationId);
        Assert.Equal(3, observed.ChoiceRevision);
        Assert.Equal(observed.ChoiceRevision, observed.ReplayRevision);
        Assert.Equal(choice, observed.Choice);
        Assert.Equal(choice == "Skip" ? "Skipped" : "Running", observed.Phase);
        Assert.Equal(choice == "UseMatch" ? "fixture-source:album-42" : null, observed.PersistedCandidate);
        Assert.Equal(1, observed.SavedIntents);
        Assert.Equal("operation-intent-mismatch", observed.ChangedReplayCode);
        Assert.Equal(observed.CatalogRevisionBefore, observed.CatalogRevisionAfter);
        Assert.Equal(0, observed.CatalogDeliveries);
        Assert.Equal(0, observed.ExternalWrites);
    }
}
