using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the independent expectations for private guided batch review.</summary>
/// <remarks>2026-11-24: Establish the TEST-LIBRARY-004 and TEST-BEETS-002 mock gate before implementation.</remarks>
/// <remarks>2026-11-26: Prove guided import review and private batch isolation for FR-BEETS-001#ac-import and FR-LIBRARY-002#ac-batch-review.</remarks>
public sealed class BatchImportReviewMockTests
{
    [Fact]
    public void RefusesPrematureReadsUnauthorizedChoicesAndCatalogEffects()
    {
        var expected = new BatchImportReviewAssertions.Evidence(true, true, true, true, true, true, true, true, true, true, true, true);
        BatchImportReviewAssertions.Verify(expected);
        BatchImportReviewAssertions.Evidence[] wrong = [expected with { IncompleteCopyHadNoReview = false }, expected with { PrivateBytesUsed = false },
            expected with { MatchProjectionScoped = false }, expected with { ForeignDenied = false }, expected with { IncompleteMatchDenied = false },
            expected with { StaleReviewDenied = false }, expected with { ChoicePersisted = false }, expected with { StableReplay = false },
            expected with { ChangedIntentDenied = false }, expected with { AllChoicesExplicit = false }, expected with { CatalogAndPlaylistsUntouched = false },
            expected with { NoDirectJobs = false }];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchImportReviewAssertions.Verify(actual));
    }
}
