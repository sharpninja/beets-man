using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Returns the editable tags and the revision of the selected catalog track.</summary>
/// <remarks>2026-11-24: Expose the source tags for FR-BEETS-001#ac-edit.</remarks>
public sealed record TrackTagsSnapshot(Guid TrackId, long CatalogRevision, ImmutableDictionary<string, string> Tags);

/// <summary>Submits a revision-pinned tag patch. A null value clears the field.</summary>
/// <remarks>2026-11-24: Preserve edit intent across retries for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed record EditTrackTagsRequest(Guid OriginInstanceId, Guid OperationId, long ExpectedCatalogRevision,
    ImmutableDictionary<string, string?> Changes);

/// <summary>Identifies the durable host job admitted for a track tag edit.</summary>
/// <remarks>2026-11-24: Return the same job after a lost acknowledgement for FR-BEETS-001#ac-recover.</remarks>
public sealed record TrackTagEditReceipt(Guid OperationId, Guid JobId, Guid LibraryId, long Revision);

/// <summary>Addresses track tags inside a registered library.</summary>
/// <remarks>2026-11-24: Define the shared read and edit route for FR-BEETS-001#ac-edit.</remarks>
public static class TrackTagRoutes
{
    /// <summary>Matches the track tag query and command.</summary>
    public const string Pattern = "/api/v1/libraries/{libraryId:guid}/tracks/{trackId:guid}/tags";

    /// <summary>Builds the track tag path for the provided library and track.</summary>
    public static string Path(Guid libraryId, Guid trackId) => $"api/v1/libraries/{libraryId:D}/tracks/{trackId:D}/tags";
}
