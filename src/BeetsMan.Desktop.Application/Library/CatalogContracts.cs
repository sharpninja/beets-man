using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Identifies the Library category being browsed.</summary>
/// <remarks>2026-11-24: Implement the request and result types for FR-LIBRARY-001#ac-browse and ac-offline.</remarks>
public enum CatalogCategory { Library, Artists, Albums, Tracks, Playlists, Search }

/// <summary>Specifies the stored catalog sort key.</summary>
public enum CatalogSortKey { TrackNumber, Title, Artist, Album, Genre, Rating, Duration }

/// <summary>Defines the criteria captured when a catalog read begins.</summary>
public sealed record CatalogFilter(string? Text = null, Guid? ArtistId = null, Guid? AlbumId = null, Guid? PlaylistId = null);

/// <summary>Defines a deterministic catalog order.</summary>
public sealed record CatalogSort(CatalogSortKey Key, bool Descending = false);

/// <summary>Describes local playback availability.</summary>
public enum TrackAvailability { Unknown, Downloaded, Pending, Unavailable }

/// <summary>Represents one stable catalog record and its presentation metadata.</summary>
public sealed record CatalogRow(Guid Id, string Title, ImmutableArray<Guid> TrackIds, string? Artist = null,
    string? Album = null, string? Genre = null, int? TrackNumber = null, int? Rating = null, TimeSpan? Duration = null,
    TrackAvailability Availability = TrackAvailability.Unknown);

/// <summary>Contains one coherent page of catalog records.</summary>
public sealed record CatalogPage(CatalogCategory Category, CatalogFilter Filter, CatalogSort Sort, long Revision,
    ImmutableArray<CatalogRow> Rows, string? NextPage = null, bool IsOffline = false);

/// <summary>Contains availability for the requested identities at one catalog revision.</summary>
public sealed record TrackAvailabilitySnapshot(long Revision, ImmutableDictionary<Guid, TrackAvailability> Tracks);

/// <summary>Contains the effective favorite and its replication status.</summary>
public sealed record FavoriteState(Guid TrackId, bool IsFavorite, long Revision, bool PendingSync);

/// <summary>Reads a filtered category or the next page of that category.</summary>
public sealed record BrowseCatalog(CatalogCategory Category, CatalogFilter Filter, CatalogSort Sort, string? Page = null) : IQuery<CatalogPage>;

/// <summary>Reads local playback availability for the specified tracks.</summary>
public sealed record GetTrackAvailability(ImmutableArray<Guid> TrackIds) : IQuery<TrackAvailabilitySnapshot>;

/// <summary>Updates one track's favorite at its expected revision.</summary>
public sealed record SetFavorite(Guid TrackId, bool IsFavorite, long ExpectedRevision) : ICommand<FavoriteState>;
