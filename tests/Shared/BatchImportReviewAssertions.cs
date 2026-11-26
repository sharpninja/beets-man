using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks the private guided choices for uploaded batch entries.</summary>
/// <remarks>2026-11-24: Cover TEST-LIBRARY-004 and TEST-BEETS-002 for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
internal static class BatchImportReviewAssertions
{
    internal sealed record Evidence(bool IncompleteCopyHadNoReview, bool PrivateBytesUsed, bool MatchProjectionScoped, bool ForeignDenied,
        bool IncompleteMatchDenied, bool StaleReviewDenied, bool ChoicePersisted, bool StableReplay, bool ChangedIntentDenied,
        bool AllChoicesExplicit, bool CatalogAndPlaylistsUntouched, bool NoDirectJobs);

    /// <summary>Verifies that each decision is original, scoped, durable and free of native write effects.</summary>
    internal static void Verify(Evidence evidence)
    {
        Assert.True(evidence.IncompleteCopyHadNoReview);
        Assert.True(evidence.PrivateBytesUsed);
        Assert.True(evidence.MatchProjectionScoped);
        Assert.True(evidence.ForeignDenied);
        Assert.True(evidence.IncompleteMatchDenied);
        Assert.True(evidence.StaleReviewDenied);
        Assert.True(evidence.ChoicePersisted);
        Assert.True(evidence.StableReplay);
        Assert.True(evidence.ChangedIntentDenied);
        Assert.True(evidence.AllChoicesExplicit);
        Assert.True(evidence.CatalogAndPlaylistsUntouched);
        Assert.True(evidence.NoDirectJobs);
    }
}
