using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Proves snapshot recovery assertions distinguish incomplete, mixed, and overbroad replacement.</summary>
/// <remarks>2026-11-24: Establish the mock gate for FR-RECORDS-001#ac-replicate, ac-notify and ac-offline.</remarks>
public sealed class CatalogSnapshotMockTests
{
    /// <summary>Rejects a deliberately altered outcome at each required observation.</summary>
    [Fact]
    public void RejectsEarlyReplacementMixedRevisionAndLostLocalState()
    {
        var expected = new CatalogSnapshotAssertions.Evidence(true, 2, ["Old"], 0, [7, 7, 7, 7, 7], 7, 1, ["Survivor"], 8, 2, [7, 8], true, true, true);
        CatalogSnapshotAssertions.Verify(expected);
        CatalogSnapshotAssertions.Evidence[] rejected =
        [
            expected with { ExpiryDetected = false }, expected with { PartialCursor = 7 }, expected with { PartialTracks = ["Survivor"] },
            expected with { EarlyAcknowledgements = 1 }, expected with { SnapshotUppers = [7, 7, 8, 8, 8] }, expected with { SnapshotCursor = 8 },
            expected with { PlaylistRevisionAtSnapshot = 2 }, expected with { SnapshotTracks = ["Old", "Survivor"] },
            expected with { RecoveredCursor = 7 }, expected with { PlaylistRevisionAfterCatchUp = 1 }, expected with { Acknowledged = [8, 8] },
            expected with { LocalDraftAndOperationRetained = false }, expected with { OtherLibraryAndAudioRetained = false },
            expected with { ForeignSnapshotDenied = false }
        ];
        Assert.All(rejected, result => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CatalogSnapshotAssertions.Verify(result)));
    }
}
