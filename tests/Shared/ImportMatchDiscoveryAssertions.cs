using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Observes the release query and the ordered comparison prepared for a staged album.</summary>
/// <remarks>2026-11-24: Cover beets candidate discovery and review data for FR-BEETS-001#ac-import and TEST-BEETS-001.</remarks>
internal sealed record ImportMatchDiscoveryObservation(string Query, string[] ReleaseKeys, string[] ReleaseTitles,
    string? FirstDate, string? FirstCountry, string[] OriginalTitles, string?[] ProposedTitles,
    bool Complete, bool IncompleteCandidateIsSelectable, bool SourceAudioPreserved, bool LiveCatalogUnchanged);

/// <summary>Uses the same expectations for independent fixtures and the native read-only review.</summary>
internal static class ImportMatchDiscoveryAssertions
{
    public const string FixtureRelease = "cf21fb29-a747-4296-935f-60b6fa2f9b9e";
    public const string AlternateRelease = "906d11e9-4a9f-487e-9e96-dcbaeee65819";

    public static void Verify(ImportMatchDiscoveryObservation observed)
    {
        Assert.Equal("release:\"Fixture Album\" AND artist:\"Fixture Band\"", observed.Query);
        Assert.Equal([$"musicbrainz:{FixtureRelease}", $"musicbrainz:{AlternateRelease}"], observed.ReleaseKeys);
        Assert.Equal(["Fixture Album", "Fixture Album (Live)"], observed.ReleaseTitles);
        Assert.Equal("2023-04-21", observed.FirstDate);
        Assert.Equal("US", observed.FirstCountry);
        Assert.Equal(["North", "South"], observed.OriginalTitles);
        Assert.Equal<string?>(new List<string?> { "North Revised", "South Revised" }, observed.ProposedTitles);
        Assert.True(observed.Complete);
        Assert.False(observed.IncompleteCandidateIsSelectable);
        Assert.True(observed.SourceAudioPreserved);
        Assert.True(observed.LiveCatalogUnchanged);
    }
}
