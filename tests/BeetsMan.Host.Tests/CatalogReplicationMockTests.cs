using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the feed assertions detect early acknowledgement, skipped pages, and replay leakage.</summary>
/// <remarks>2026-11-24: Establish the independent BDPv4 mock gate for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
public sealed class CatalogReplicationMockTests
{
    /// <summary>Rejects a deliberately incorrect outcome at each replicated effect.</summary>
    [Fact]
    public void RejectsUncommittedAcknowledgementAndDuplicateOrMissingRecords()
    {
        var fixture = new CatalogReplicationAssertions.Evidence([2, 2, 3], 2, 3, [2, 3], ["Keep"], ["Keep", "Keep"], 1, true,
            [], [], [1, 2, 3], "catalog-ack-host-unreachable", true, true, true);
        CatalogReplicationAssertions.Verify(fixture);
        CatalogReplicationAssertions.Evidence[] rejected =
        [
            fixture with { FixedUpperPages = [2, 3, 3] }, fixture with { CursorAtFailedAck = 1 }, fixture with { CursorAfterRetry = 2 },
            fixture with { AcknowledgedAttempts = [3, 3] }, fixture with { LocalTitles = ["Old", "Keep"] },
            fixture with { SavedPlaylistEntries = ["Keep"] }, fixture with { SavedPlaylistRevision = 3 },
            fixture with { DeletedTrackStaysDeleted = false }, fixture with { OriginPending = [1] }, fixture with { OnlinePeerPending = [2] },
            fixture with { OfflinePeerPending = [3] }, fixture with { FailureCode = "acknowledged" }, fixture with { TamperedPageRolledBack = false },
            fixture with { ScopeEnforced = false }, fixture with { LocalAudioRetained = false }
        ];
        Assert.All(rejected, value => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CatalogReplicationAssertions.Verify(value)));
    }
}
