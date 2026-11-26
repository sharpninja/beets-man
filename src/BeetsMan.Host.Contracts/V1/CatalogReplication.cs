using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies the synchronized catalog record family.</summary>
public enum CatalogEntityKind { Unspecified, Artist, Album, Track, Playlist, Favorite, Play, SharedDefault }

/// <summary>Distinguishes an explicit deletion from a missing or malformed upsert.</summary>
public enum CatalogEntityOperation { Unspecified, Upsert, Delete }

/// <summary>Contains the replicated fields of one catalog entity.</summary>
/// <remarks>2026-11-24: Share the versioned catalog projection for FR-RECORDS-001#ac-replicate.</remarks>
[JsonDerivedType(typeof(ReplicaArtist), "artist")]
[JsonDerivedType(typeof(ReplicaAlbum), "album")]
[JsonDerivedType(typeof(ReplicaTrack), "track")]
[JsonDerivedType(typeof(ReplicaPlaylist), "playlist")]
[JsonDerivedType(typeof(ReplicaFavorite), "favorite")]
[JsonDerivedType(typeof(ReplicaPlay), "play")]
[JsonDerivedType(typeof(ReplicaSharedDefault), "shared-default")]
public abstract record CatalogReplicaEntity;

/// <summary>Contains the displayed and sorted artist names.</summary>
public sealed record ReplicaArtist(string Name, string? SortName) : CatalogReplicaEntity;

/// <summary>Contains shared album grouping and display fields.</summary>
public sealed record ReplicaAlbum(string Title, string? SortTitle, Guid? AlbumArtistId) : CatalogReplicaEntity;

/// <summary>Contains shared track grouping, relative media path, metadata, and explicit tag overrides.</summary>
public sealed record ReplicaTrack(string Title, string RelativePath, Guid? ArtistId, Guid? AlbumId, string? SortTitle, string? Genre,
    int? TrackNumber, int? DiscNumber, int? Year, int? Rating, long? DurationTicks, bool HasAlbumTag, string? AlbumTag,
    bool HasAlbumArtistTag, string? AlbumArtistTag) : CatalogReplicaEntity;

/// <summary>Contains the saved playlist revision and immutable ordered entry identities.</summary>
public sealed record ReplicaPlaylist(string Name, long PlaylistRevision, ImmutableArray<PlaylistEntryData> Entries) : CatalogReplicaEntity;

/// <summary>Identifies a track that is a favorite in the shared listener profile.</summary>
public sealed record ReplicaFavorite(Guid TrackId) : CatalogReplicaEntity;

/// <summary>Contains one immutable play and its timestamp.</summary>
public sealed record ReplicaPlay(Guid TrackId, DateTimeOffset PlayedAtUtc) : CatalogReplicaEntity;

/// <summary>Contains a registered typed default shared across the library.</summary>
/// <remarks>2026-11-25: Replicate typed notification flags for FR-SETTINGS-001#ac-availability.</remarks>
/// <remarks>2026-11-25: Replicate typed Appearance theme data and validate its key for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed record ReplicaSharedDefault(string Key, int RegistryVersion, long? DurationTicks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Flag = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null) : CatalogReplicaEntity
{
    /// <summary>Checks registry version and payload for a live row in a feed or snapshot.</summary>
    public bool IsValidUpsert() => RegistryVersion == SharedLibraryDefaults.RegistryVersion &&
        (DurationTicks is not null || Flag is not null || Text is not null) && SharedLibraryDefaults.IsValid(Key, DurationTicks, Flag, Text);
}

/// <summary>Describes one active entity or explicit tombstone in a revision.</summary>
public sealed record CatalogFeedChange(CatalogEntityKind Kind, CatalogEntityOperation Operation, Guid RecordId, CatalogReplicaEntity? Entity = null);

/// <summary>Associates a complete revision with its original registered operation.</summary>
public sealed record CatalogFeedRevision(long Revision, Guid OriginInstanceId, Guid OperationId, ImmutableArray<CatalogFeedChange> Changes);

/// <summary>Captures a fixed upper revision and a contiguous forward page for one recipient.</summary>
public sealed record CatalogFeedPage(Guid LibraryId, Guid RecipientInstanceId, long AfterRevision, long ThroughRevision, long UpperRevision,
    ImmutableArray<CatalogFeedRevision> Revisions);

/// <summary>Reports the ordered pending revisions for the authenticated recipient.</summary>
public sealed record CatalogNotifications(Guid LibraryId, Guid RecipientInstanceId, ImmutableArray<long> PendingRevisions);

/// <summary>Acknowledges the recipient's contiguous committed catalog checkpoint.</summary>
public sealed record CatalogAcknowledgement(Guid RecipientInstanceId, long ThroughRevision);

/// <summary>Identifies the acknowledged library, recipient, and revision.</summary>
public sealed record CatalogAcknowledgementReceipt(Guid LibraryId, Guid RecipientInstanceId, long ThroughRevision);

/// <summary>Addresses the versioned incremental feed and recipient-specific notification endpoints.</summary>
/// <remarks>2026-11-24: Provide FR-RECORDS-001#ac-replicate and ac-notify over authenticated HTTPS.</remarks>
public static class CatalogReplicationRoutes
{
    public const string ChangesPattern = "/api/v1/libraries/{libraryId:guid}/catalog/changes";
    public const string NotificationsPattern = "/api/v1/libraries/{libraryId:guid}/notifications";
    public const string AcknowledgePattern = NotificationsPattern + "/acknowledge";

    /// <summary>Addresses a cursor and optional previously captured upper revision.</summary>
    public static string ChangesPath(Guid library, long after, long? upper, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"api/v1/libraries/{library:D}/catalog/changes?after={after}&limit={limit}") +
        (upper.HasValue ? "&upper=" + upper.Value.ToString(CultureInfo.InvariantCulture) : "");

    /// <summary>Addresses the pending notification list for the authenticated instance.</summary>
    public static string NotificationsPath(Guid library) => $"api/v1/libraries/{library:D}/notifications";

    /// <summary>Addresses the authenticated recipient's acknowledgement operation.</summary>
    public static string AcknowledgePath(Guid library) => NotificationsPath(library) + "/acknowledge";
}

/// <summary>Checks that a versioned catalog page is ordered, scoped and complete before any local mutations.</summary>
/// <remarks>2026-11-24: Prevent an invalid or skipped revision from advancing FR-RECORDS-001#ac-replicate.</remarks>
public static class CatalogFeedProtocol
{
    public const int MaximumPageSize = 100;

    /// <summary>Verifies every revision and entity against the requested contiguous window.</summary>
    public static bool IsValid(CatalogFeedPage page) => page.LibraryId != Guid.Empty && page.RecipientInstanceId != Guid.Empty &&
        page.AfterRevision >= 0 && page.ThroughRevision >= page.AfterRevision && page.UpperRevision >= page.ThroughRevision &&
        !page.Revisions.IsDefault && page.Revisions.Length <= MaximumPageSize && page.ThroughRevision - page.AfterRevision == page.Revisions.Length &&
        (!page.Revisions.IsEmpty || page.ThroughRevision == page.UpperRevision) &&
        page.Revisions.Select((revision, index) => ValidRevision(revision, page.LibraryId, page.AfterRevision + index + 1)).All(valid => valid);

    private static bool ValidRevision(CatalogFeedRevision revision, Guid library, long expected) => revision is not null && revision.Revision == expected &&
        revision.OriginInstanceId != Guid.Empty && revision.OperationId != Guid.Empty && !revision.Changes.IsDefaultOrEmpty &&
        revision.Changes.All(change => ValidChange(change, library)) &&
        revision.Changes.Select(change => (change.Kind, change.RecordId)).Distinct().Count() == revision.Changes.Length;

    /// <summary>Validates a versioned entity or tombstone shared by feed and snapshot reads.</summary>
    /// <remarks>2026-11-25: Accept only registered theme mutations and tombstones for FR-SETTINGS-001#ac-availability.</remarks>
    internal static bool ValidChange(CatalogFeedChange change, Guid library) => change is not null && change.RecordId != Guid.Empty && (change.Operation switch
    {
        CatalogEntityOperation.Delete => change.Kind != CatalogEntityKind.Unspecified && Enum.IsDefined(change.Kind) && change.Entity is null &&
            (change.Kind != CatalogEntityKind.SharedDefault ||
                change.RecordId == LibraryRecordIdentity.SharedDefault(library, SharedLibraryDefaults.AutomaticFadeDuration) ||
                change.RecordId == LibraryRecordIdentity.SharedDefault(library, SharedLibraryDefaults.SyncCompleted) ||
                change.RecordId == LibraryRecordIdentity.SharedDefault(library, SharedLibraryDefaults.Theme)),
        CatalogEntityOperation.Upsert => (change.Kind, change.Entity) switch
        {
            (CatalogEntityKind.Artist, ReplicaArtist artist) => !string.IsNullOrWhiteSpace(artist.Name),
            (CatalogEntityKind.Album, ReplicaAlbum album) => !string.IsNullOrWhiteSpace(album.Title) && album.AlbumArtistId != Guid.Empty,
            (CatalogEntityKind.Track, ReplicaTrack track) => track.Title is not null && ValidPath(track.RelativePath) &&
                track.ArtistId != Guid.Empty && track.AlbumId != Guid.Empty && (track.HasAlbumTag || track.AlbumTag is null) &&
                (track.HasAlbumArtistTag || track.AlbumArtistTag is null) && track.DurationTicks is not < 0,
            (CatalogEntityKind.Playlist, ReplicaPlaylist playlist) => !string.IsNullOrWhiteSpace(playlist.Name) && playlist.PlaylistRevision > 0 &&
                !playlist.Entries.IsDefault && playlist.Entries.All(entry => entry is not null && entry.EntryId != Guid.Empty && entry.TrackId != Guid.Empty) &&
                playlist.Entries.Select(entry => entry.EntryId).Distinct().Count() == playlist.Entries.Length,
            (CatalogEntityKind.Favorite, ReplicaFavorite favorite) => favorite.TrackId != Guid.Empty &&
                change.RecordId == LibraryRecordIdentity.Favorite(library, favorite.TrackId),
            (CatalogEntityKind.Play, ReplicaPlay play) => play.TrackId != Guid.Empty && play.PlayedAtUtc.Offset == TimeSpan.Zero && play.PlayedAtUtc.UtcTicks > 0,
            (CatalogEntityKind.SharedDefault, ReplicaSharedDefault setting) => setting.IsValidUpsert() &&
                change.RecordId == LibraryRecordIdentity.SharedDefault(library, setting.Key),
            _ => false
        },
        _ => false
    });

    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains(':') && !path.Contains('\\') &&
        !path.Any(char.IsControl) && path.Split('/').All(segment => segment is not ("" or "." or ".."));
}
