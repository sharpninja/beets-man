using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies the client state that submitted a playlist save.</summary>
/// <remarks>2026-11-24: Scope version 1 playlist submissions for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public enum PlaylistSaveSourceKind { Draft = 1, Queue = 2 }

/// <summary>Preserves the identity of the originating draft or playback session across a host round trip.</summary>
public sealed record PlaylistSaveSource(PlaylistSaveSourceKind Kind, Guid SourceId);

/// <summary>Identifies one ordered appearance of a track in a saved playlist.</summary>
public sealed record PlaylistEntryData(Guid EntryId, Guid TrackId);

/// <summary>Submits a versioned ordered playlist save and its durable client operation identity.</summary>
public sealed record PlaylistSaveRequest(Guid OriginInstanceId, Guid OperationId, PlaylistSaveSource Source,
    Guid? PlaylistId, long? ExpectedRevision, string Name, ImmutableArray<PlaylistEntryData> Entries);

/// <summary>Returns the committed playlist version and the associated library feed revision.</summary>
public sealed record PlaylistSaveResponse(Guid OperationId, PlaylistSaveSource Source, Guid PlaylistId,
    long PlaylistRevision, long FeedRevision);

/// <summary>Describes a versioned playlist request rejection or dependency failure.</summary>
public sealed record PlaylistSaveProblem(string Code, long? ActualRevision = null);

/// <summary>Contains the shared version 1 endpoint route for saving a playlist.</summary>
/// <remarks>2026-11-24: Bind playlist save transport for FR-RECORDS-001#ac-offline.</remarks>
public static class PlaylistPublicationRoute
{
    /// <summary>Identifies the host route template for playlist publication.</summary>
    public const string Pattern = "/api/v1/libraries/{libraryId:guid}/playlists/save";

    /// <summary>Returns the relative request path scoped to one library.</summary>
    public static string Path(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/playlists/save";
}
