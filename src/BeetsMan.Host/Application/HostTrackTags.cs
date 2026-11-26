using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Reads a registered library track and journals a validated edit against its revision.</summary>
/// <remarks>2026-11-24: Keep tag reads and admissions scoped for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal interface ITrackTagStore
{
    Task<HostOutcome<TrackTagsSnapshot>> ReadAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller, CancellationToken token);
    Task<HostOutcome<TrackTagEditReceipt>> AcceptAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller,
        Guid operationId, long expectedRevision, ImmutableSortedDictionary<string, string?> changes, CancellationToken token);
}

/// <summary>Checks the declared edit operation and normalizes the wire patch for the repository.</summary>
/// <remarks>2026-11-24: Validate the eight SCR39 editable fields for FR-BEETS-001#ac-edit.</remarks>
internal sealed class HostTrackTags(ITrackTagStore store)
{
    /// <summary>Reads the tags that belong to the selected track in this library.</summary>
    public Task<HostOutcome<TrackTagsSnapshot>> ReadAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller, CancellationToken token) =>
        store.ReadAsync(libraryId, trackId, caller, token);

    /// <summary>Accepts a valid patch using its canonical field names and values.</summary>
    public Task<HostOutcome<TrackTagEditReceipt>> AcceptAsync(Guid libraryId, Guid trackId, AuthenticatedHostInstance caller,
        EditTrackTagsRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<TrackTagEditReceipt>>(new HostOutcome<TrackTagEditReceipt>.Forbidden());
        if (request.OperationId == Guid.Empty || request.ExpectedCatalogRevision <= 0 || request.Changes is not { Count: > 0 } ||
            !TryNormalize(request.Changes, out var changes))
            return Task.FromResult<HostOutcome<TrackTagEditReceipt>>(new HostOutcome<TrackTagEditReceipt>.Rejected("invalid-track-tag-edit"));
        return store.AcceptAsync(libraryId, trackId, caller, request.OperationId, request.ExpectedCatalogRevision, changes, token);
    }

    /// <summary>Normalizes editable metadata fields for FR-BEETS-001#ac-edit and FR-LIBRARY-002#ac-batch-commit.</summary>
    internal static bool TryNormalize(ImmutableDictionary<string, string?> input, out ImmutableSortedDictionary<string, string?> changes)
    {
        changes = ImmutableSortedDictionary<string, string?>.Empty.WithComparers(StringComparer.Ordinal);
        foreach (var (name, value) in input)
        {
            var key = name.ToLowerInvariant();
            if (key is not ("title" or "artist" or "albumartist" or "album" or "year" or "disc" or "track" or "genre") ||
                changes.ContainsKey(key) || value is { Length: > 4096 } || (value is not null && value.Any(char.IsControl)))
                return false;
            var normalized = value;
            if (value is not null && key is ("year" or "disc" or "track"))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                    (key == "year" && number > 9999))
                    return false;
                normalized = number.ToString(CultureInfo.InvariantCulture);
            }
            changes = changes.Add(key, normalized);
        }
        return true;
    }
}
