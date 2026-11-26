using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the album review assertions independently of the journal implementation.</summary>
/// <remarks>2026-11-24: Establish the mock gate for FR-BEETS-001#ac-import and ac-recover.</remarks>
public sealed class GuidedImportReviewMockTests
{
    /// <summary>Rejects incorrect album grouping, stale decisions, scope leaks, and a reminted review on restart.</summary>
    [Fact]
    public void RequiresTheSameAuthorizedReviewAfterRestart()
    {
        var observed = new ScriptedAlbumReview().ReadAfterRestart();
        GuidedImportReviewAssertions.VerifyReview(observed);
        RejectReview(observed with { RestartedReviewItemId = Guid.NewGuid() });
        RejectReview(observed with { PhaseBeforeChoice = "Running" });
        RejectReview(observed with { SourceNames = ["North.flac"] });
        RejectReview(observed with { CandidateTitles = ["Alternate Album", "Fixture Album"] });
        RejectReview(observed with { RestartedReviewRevision = 3 });
        RejectReview(observed with { ForeignReadStatus = 200 });
        RejectReview(observed with { ForeignChoiceStatus = 200 });
        RejectReview(observed with { StaleChoiceCode = "accepted" });
        RejectReview(observed with { UnknownMatchCode = "accepted" });
    }

    /// <summary>Rejects unjournaled, duplicate, prematurely published, or externally executed decisions.</summary>
    [Theory]
    [InlineData("UseMatch")]
    [InlineData("KeepTags")]
    [InlineData("Skip")]
    public void RequiresOneDurableChoiceBeforeExternalEffects(string choice)
    {
        var observed = new ScriptedAlbumReview().AcceptChoice(choice);
        GuidedImportReviewAssertions.VerifyChoice(observed, choice);
        RejectChoice(observed with { ReceiptJobId = Guid.NewGuid() }, choice);
        RejectChoice(observed with { ReplayOperationId = Guid.NewGuid() }, choice);
        RejectChoice(observed with { ReplayRevision = 4 }, choice);
        RejectChoice(observed with { Phase = "Published" }, choice);
        RejectChoice(observed with { PersistedCandidate = "alternate-source:wrong-album" }, choice);
        RejectChoice(observed with { SavedIntents = 2 }, choice);
        RejectChoice(observed with { ChangedReplayCode = "accepted" }, choice);
        RejectChoice(observed with { CatalogRevisionAfter = 42 }, choice);
        RejectChoice(observed with { CatalogDeliveries = 1 }, choice);
        RejectChoice(observed with { ExternalWrites = 1 }, choice);
    }

    private static void RejectReview(GuidedImportReviewObservation evidence) =>
        Assert.ThrowsAny<XunitException>(() => GuidedImportReviewAssertions.VerifyReview(evidence));

    private static void RejectChoice(GuidedImportChoiceObservation evidence, string choice) =>
        Assert.ThrowsAny<XunitException>(() => GuidedImportReviewAssertions.VerifyChoice(evidence, choice));

    /// <summary>Scripts the storyboard's ordered album sources, two candidates, and one decision per test case.</summary>
    private sealed class ScriptedAlbumReview
    {
        private readonly Guid job = new("9965444c-2728-45ba-9347-47bc470b793f");
        private readonly Guid album = new("d744e793-9c95-407c-911d-578fccb9914b");
        private readonly Guid operation = new("04d9dba1-232c-4ffa-b7e4-bbc2900f918a");

        public GuidedImportReviewObservation ReadAfterRestart() => new(job, album, album, "ReviewRequired",
            ["North.flac", "South.flac", "Harbor.flac"], ["Fixture Album", "Alternate Album"], 2, 2,
            404, 404, "stale-import-review", "unknown-import-match");

        public GuidedImportChoiceObservation AcceptChoice(string choice) => new(job, job, operation, operation, 3, 3, choice,
            choice == "Skip" ? "Skipped" : "Running", choice == "UseMatch" ? "fixture-source:album-42" : null,
            1, "operation-intent-mismatch", 41, 41, 0, 0);
    }
}
