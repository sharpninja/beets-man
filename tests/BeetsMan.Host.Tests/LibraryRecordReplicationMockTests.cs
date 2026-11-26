using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Checks independent expectations for authorized listening records, defaults and replay.</summary>
/// <remarks>2026-11-25: Retain the independent mock gate for TEST-RECORDS-001 and TEST-RECORDS-002.</remarks>
/// <remarks>2026-11-26: Reject incorrect replication and recipient delivery results for FR-RECORDS-001#ac-replicate and FR-RECORDS-001#ac-notify.</remarks>
public sealed class LibraryRecordReplicationMockTests
{
    [Fact]
    public void RejectsDuplicatePublicationAndInvalidReplicas()
    {
        var expected = new LibraryRecordReplicationAssertions.Evidence(6, 1, 1, 2, true, true, true, true, true, true, true);
        LibraryRecordReplicationAssertions.Verify(expected);
        LibraryRecordReplicationAssertions.Evidence[] wrong =
        [
            expected with { FeedHead = 7 }, expected with { FavoriteDeletes = 0 }, expected with { DefaultDeletes = 0 },
            expected with { DistinctPlays = 1 }, expected with { AllReplaysStable = false }, expected with { StaleFavoriteRejected = false },
            expected with { PlayIdentityConflictRejected = false }, expected with { LocalOnlyDefaultRejected = false },
            expected with { ForeignOriginRejected = false }, expected with { FeedAndSnapshotAgree = false },
            expected with { UnrelatedLocalStatePreserved = false }
        ];
        foreach (var result in wrong)
            Assert.ThrowsAny<XunitException>(() => LibraryRecordReplicationAssertions.Verify(result));
    }
}
