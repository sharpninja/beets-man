using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises review assertions against independent lookup and comparison fixtures.</summary>
/// <remarks>2026-11-24: Establish the candidate discovery mock gate for FR-BEETS-001#ac-import.</remarks>
public sealed class ImportMatchDiscoveryMockTests
{
    [Fact]
    public void RejectsAChangedOrIncompleteComparisonAndPrematureWrites()
    {
        var observed = new ImportMatchDiscoveryObservation("release:\"Fixture Album\" AND artist:\"Fixture Band\"",
            [$"musicbrainz:{ImportMatchDiscoveryAssertions.FixtureRelease}", $"musicbrainz:{ImportMatchDiscoveryAssertions.AlternateRelease}"],
            ["Fixture Album", "Fixture Album (Live)"], "2023-04-21", "US", ["North", "South"], ["North Revised", "South Revised"],
            true, false, true, true);
        ImportMatchDiscoveryAssertions.Verify(observed);
        Reject(observed with { Query = observed.Query + " AND path:/private" });
        Reject(observed with { ReleaseKeys = [observed.ReleaseKeys[1], observed.ReleaseKeys[0]] });
        Reject(observed with { FirstDate = null });
        Reject(observed with { FirstCountry = "GB" });
        Reject(observed with { OriginalTitles = ["South", "North"] });
        Reject(observed with { ProposedTitles = ["North Revised", null] });
        Reject(observed with { Complete = false });
        Reject(observed with { IncompleteCandidateIsSelectable = true });
        Reject(observed with { SourceAudioPreserved = false });
        Reject(observed with { LiveCatalogUnchanged = false });
    }

    private static void Reject(ImportMatchDiscoveryObservation observed) =>
        Assert.ThrowsAny<XunitException>(() => ImportMatchDiscoveryAssertions.Verify(observed));
}
