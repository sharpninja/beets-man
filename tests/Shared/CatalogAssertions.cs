using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Describes shared approved examples for cached catalog and search behavior.</summary>
/// <remarks>2026-11-24: Verify TEST-LIBRARY-001/002 for FR-LIBRARY-001#ac-browse and ac-offline.</remarks>
internal static class CatalogAssertions
{
    public static readonly Guid Artist = Guid.Parse("61000000-0000-0000-0000-000000000001");
    public static readonly Guid Album = Guid.Parse("62000000-0000-0000-0000-000000000001");
    public static readonly Guid Downloaded = Guid.Parse("63000000-0000-0000-0000-000000000001");
    public static readonly Guid Pending = Guid.Parse("63000000-0000-0000-0000-000000000002");
    public static readonly Guid Playlist = Guid.Parse("64000000-0000-0000-0000-000000000001");
    public const long Revision = 7;

    public static readonly ImmutableArray<BrowseCatalog> Queries =
    [
        new(CatalogCategory.Artists, new(Text: "violet"), new(CatalogSortKey.Title)),
        new(CatalogCategory.Albums, new(ArtistId: Artist), new(CatalogSortKey.Title)),
        new(CatalogCategory.Tracks, new(AlbumId: Album), new(CatalogSortKey.TrackNumber)),
        new(CatalogCategory.Playlists, new(Text: "Evening"), new(CatalogSortKey.Title)),
        new(CatalogCategory.Search, new(Text: "ViOlEt"), new(CatalogSortKey.TrackNumber))
    ];

    public static void VerifyPages(ImmutableArray<CatalogPage> results)
    {
        Assert.Equal(5, results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(Queries[i].Category, results[i].Category);
            Assert.Equal(Queries[i].Filter, results[i].Filter);
            Assert.Equal(Queries[i].Sort, results[i].Sort);
            Assert.Equal(Revision, results[i].Revision);
            Assert.True(results[i].IsOffline);
        }
        Assert.Equal(Artist, Assert.Single(results[0].Rows).Id);
        Assert.Equal(Album, Assert.Single(results[1].Rows).Id);
        Assert.Equal([Downloaded, Pending], results[2].Rows.Select(row => row.Id));
        var playlist = Assert.Single(results[3].Rows);
        Assert.Equal(Playlist, playlist.Id);
        Assert.Equal([Pending, Downloaded, Downloaded], playlist.TrackIds);
        Assert.Equal([Downloaded, Pending], results[4].Rows.Select(row => row.Id));
        Assert.Equal(TrackAvailability.Downloaded, results[4].Rows[0].Availability);
        Assert.Equal(TrackAvailability.Pending, results[4].Rows[1].Availability);
    }

    public static void VerifyAvailability(TrackAvailabilitySnapshot snapshot)
    {
        Assert.Equal(Revision, snapshot.Revision);
        Assert.Equal(2, snapshot.Tracks.Count);
        Assert.Equal(TrackAvailability.Downloaded, snapshot.Tracks[Downloaded]);
        Assert.Equal(TrackAvailability.Pending, snapshot.Tracks[Pending]);
    }

    public static void VerifyCursorConflict(Outcome<CatalogPage> outcome, long revision)
    {
        var failure = Assert.IsType<Outcome<CatalogPage>.Conflict>(outcome);
        Assert.Equal("stale-catalog-cursor", failure.Code);
        Assert.Equal(revision, failure.ActualRevision);
    }
}
