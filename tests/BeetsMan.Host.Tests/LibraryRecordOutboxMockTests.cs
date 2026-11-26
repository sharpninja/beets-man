using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies independent assertions for offline operations and conflict resolution.</summary>
/// <remarks>2026-11-25: Establish the mock gate for TEST-RECORDS-001 and TEST-RECORDS-002.</remarks>
/// <remarks>2026-11-26: Reject lost offline intent, conflicts and play identities for FR-RECORDS-001#ac-offline.</remarks>
public sealed class LibraryRecordOutboxMockTests
{
    [Fact]
    public void RejectsLostLocalIntentsOrAnUnreviewedConflictResolution()
    {
        var good = new LibraryRecordOutboxAssertions.Evidence(3, false, true, 1, 1, 0, true, true, true, true);
        LibraryRecordOutboxAssertions.Verify(good);
        LibraryRecordOutboxAssertions.Evidence[] wrong =
        [
            good with { PendingBeforeReconnect = 2 }, good with { LatestOfflineFavorite = true }, good with { ReplayedOriginalOperations = false },
            good with { SecondFavoriteBaseline = 0 }, good with { DistinctPlayedIds = 2 }, good with { PendingAfterReplay = 1 },
            good with { RetriedLostReply = false }, good with { ConflictSurvivesSnapshot = false }, good with { ReappliedAtReviewedRevision = false },
            good with { KeepHostClearsDraft = false }
        ];
        foreach (var value in wrong)
            Assert.ThrowsAny<XunitException>(() => LibraryRecordOutboxAssertions.Verify(value));
    }
}
