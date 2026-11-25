using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using Xunit;
using Xunit.Sdk;
using static BeetsMan.Desktop.Tests.CatalogAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks browsing acceptance expectations before binding the catalog repositories.</summary>
/// <remarks>2026-11-24: Exercise the independent TEST-LIBRARY-001 fixture and negative controls.</remarks>
public sealed class CatalogMockTests
{
    [Fact]
    public void CatalogAssertionsRejectOutOfOrderAndUnavailableOfflineRows()
    {
        CatalogRow first = new(Downloaded, "First", [Downloaded], TrackNumber: 1, Availability: TrackAvailability.Downloaded);
        CatalogRow second = new(Pending, "Second", [Pending], TrackNumber: 2, Availability: TrackAvailability.Pending);
        ImmutableArray<ImmutableArray<CatalogRow>> rows =
        [
            [new(Artist, "Violet", [Downloaded, Pending])],
            [new(Album, "Set", [Downloaded, Pending])],
            [first, second],
            [new(Playlist, "Evening", [Pending, Downloaded, Downloaded])],
            [first, second]
        ];
        var pages = Queries.Select((query, index) =>
            new CatalogPage(query.Category, query.Filter, query.Sort, Revision, rows[index], IsOffline: true)).ToImmutableArray();
        VerifyPages(pages);
        Assert.ThrowsAny<XunitException>(() => VerifyPages(pages.SetItem(2, pages[2] with { Rows = [second, first] })));
        Assert.ThrowsAny<XunitException>(() => VerifyPages(pages.SetItem(3, pages[3] with { Rows = [new(Playlist, "Evening", [Downloaded, Pending])] })));
        Assert.ThrowsAny<XunitException>(() => VerifyPages(pages.SetItem(4, pages[4] with { Rows = [first, second with { Availability = TrackAvailability.Downloaded }] })));

        TrackAvailabilitySnapshot status = new(Revision, ImmutableDictionary<Guid, TrackAvailability>.Empty
            .Add(Downloaded, TrackAvailability.Downloaded).Add(Pending, TrackAvailability.Pending));
        VerifyAvailability(status);
        Assert.ThrowsAny<XunitException>(() => VerifyAvailability(status with { Tracks = status.Tracks.Remove(Pending) }));
    }

    [Fact]
    public void CursorAssertionsRejectPagingAcrossProjectionRevisions()
    {
        VerifyCursorConflict(new Outcome<CatalogPage>.Conflict("stale-catalog-cursor", Revision + 1), Revision + 1);
        Assert.ThrowsAny<XunitException>(() => VerifyCursorConflict(new Outcome<CatalogPage>.Conflict("stale-catalog-cursor", Revision), Revision + 1));
    }
}
