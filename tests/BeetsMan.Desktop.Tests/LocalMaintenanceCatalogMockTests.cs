using BeetsMan.Desktop.Application.Library;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the catalog maintenance assertions distinguish the intended effects from stale, shared, and partial projections.</summary>
/// <remarks>2026-11-24: Establish the independent mock gate for TEST-LIBRARY-003 and FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed class LocalMaintenanceCatalogMockTests
{
    /// <summary>Rejects incomplete projection and revert evidence for staged maintenance.</summary>
    [Fact]
    public void RejectsPartialCatalogProjectionAndRevert()
    {
        var evidence = new LocalMaintenanceCatalogAssertions.Evidence(["First"], [], ["First"], ["First"], ["First", "First"],
            "Organized/first.flac", TrackAvailability.Downloaded, "Set/first.flac", TrackAvailability.Unknown, null, true,
            "stale-catalog-cursor", "stale-catalog-cursor", ["First", "Second"], true, ["First", "Second"], "Set/first.flac", true);
        LocalMaintenanceCatalogAssertions.Verify(evidence);
        LocalMaintenanceCatalogAssertions.Evidence[] rejected =
        [
            evidence with { Tracks = ["First", "Second"] },
            evidence with { Search = ["Second"] },
            evidence with { ArtistMembers = ["First", "Second"] },
            evidence with { AlbumMembers = ["First", "Second"] },
            evidence with { PlaylistMembers = ["Second", "First", "First"] },
            evidence with { PlaylistMembers = ["First"] },
            evidence with { ProposedPath = "Set/first.flac" },
            evidence with { PlaybackAvailability = TrackAvailability.Pending },
            evidence with { PlaybackPath = "Organized/first.flac" },
            evidence with { RemovedAvailability = TrackAvailability.Downloaded },
            evidence with { RemovedPlaybackPath = "Set/missing.flac" },
            evidence with { FeedRevisionPreserved = false },
            evidence with { BeforeStageCursor = "accepted" },
            evidence with { BeforeRevertCursor = "accepted" },
            evidence with { AnotherDesktopTracks = ["First"] },
            evidence with { RestartRetainedEffects = false },
            evidence with { RevertedTracks = ["First"] },
            evidence with { RevertedPath = "Organized/first.flac" },
            evidence with { BaseAndFilesUnchanged = false }
        ];
        Assert.All(rejected, item => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => LocalMaintenanceCatalogAssertions.Verify(item)));
    }
}
