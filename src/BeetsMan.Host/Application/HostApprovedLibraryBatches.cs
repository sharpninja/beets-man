using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Validates the exact intent and delegates whole-batch admission to the authoritative transaction.</summary>
/// <remarks>2026-11-24: Reject partial or changed review submissions for FR-LIBRARY-002#ac-batch-review and ac-batch-commit.</remarks>
internal sealed class HostApprovedLibraryBatches(IApprovedLibraryBatchStore store)
{
    /// <summary>Accepts only a fully described and explicitly approved review.</summary>
    public Task<HostOutcome<ApprovedLibraryBatchStatus>> SubmitAsync(Guid library, AuthenticatedHostInstance caller, ApproveLibraryBatchRequest request,
        CancellationToken token)
    {
        if (caller.InstanceId != request.OriginInstanceId)
            return Task.FromResult<HostOutcome<ApprovedLibraryBatchStatus>>(new HostOutcome<ApprovedLibraryBatchStatus>.Forbidden());
        if (request.OperationId == Guid.Empty || request.BatchId == Guid.Empty || request.BatchRevision <= 0 || request.ReviewId == Guid.Empty ||
            request.Entries.IsDefaultOrEmpty || !request.Entries.Select((entry, index) => entry is not null && entry.Position == index + 1 &&
                entry.OperationId != Guid.Empty && entry.OperationId != request.OperationId && Valid(library, request.OriginInstanceId, entry)).All(valid => valid) ||
            request.Entries.Select(entry => entry.OperationId).Distinct().Count() != request.Entries.Length)
            return Task.FromResult<HostOutcome<ApprovedLibraryBatchStatus>>(new HostOutcome<ApprovedLibraryBatchStatus>.Rejected("invalid-approved-library-batch"));
        return store.SubmitBatchAsync(library, caller, request, token);
    }

    private static bool Valid(Guid library, Guid origin, ApprovedBatchEntry entry) => entry.Change switch
    {
        ApprovedBatchChange.TrackTags tags => tags.TrackId != Guid.Empty && tags.ExpectedRevision > 0 && tags.Before is not null &&
            tags.Changes is { Count: > 0 } && HostTrackTags.TryNormalize(tags.Changes.ToImmutableDictionary(StringComparer.Ordinal), out var canonical) &&
            tags.Changes.SequenceEqual(canonical),
        ApprovedBatchChange.Import import => !import.Files.IsDefaultOrEmpty && import.Files.All(file => file is not null && file.FileId != Guid.Empty &&
            HostImportIntake.ValidManifestFile(file.Manifest)) && import.Files.Select(file => file.FileId).Distinct().Count() == import.Files.Length,
        ApprovedBatchChange.Maintenance maintenance => maintenance.Plan is { } plan && plan.PlanId != Guid.Empty && plan.LibraryId == library &&
            plan.CatalogRevision > 0 && plan.Kind is LibraryMaintenanceKind.Organize or LibraryMaintenanceKind.RemoveCatalog or LibraryMaintenanceKind.RemoveAudio &&
            (plan.Kind == LibraryMaintenanceKind.RemoveAudio) == maintenance.AudioDeletionApproved &&
            !plan.TrackIds.IsDefaultOrEmpty && !plan.Moves.IsDefault && !plan.AudioPaths.IsDefault,
        ApprovedBatchChange.Playlist playlist => playlist.SourceRevision > 0 && HostPlaylistWriter.IsValid(
            new(origin, entry.OperationId, playlist.Source, playlist.PlaylistId, playlist.ExpectedRevision, playlist.Name, playlist.Entries)),
        _ => false
    };

    /// <summary>Reads the submission through the same registered owner boundary.</summary>
    public Task<HostOutcome<ApprovedLibraryBatchStatus>> InspectAsync(Guid library, AuthenticatedHostInstance caller, Guid operation,
        CancellationToken token) => store.ReadBatchAsync(library, caller, operation, token);

    /// <summary>Fences an exact approval before native effects can begin, including before host admission.</summary>
    /// <remarks>2026-11-25: Resolve lost submissions safely for FR-LIBRARY-002#ac-batch-revert.</remarks>
    public Task<HostOutcome<WithdrawApprovedLibraryBatchReceipt>> WithdrawAsync(Guid library, AuthenticatedHostInstance caller, Guid operation,
        WithdrawApprovedLibraryBatchRequest request, CancellationToken token)
    {
        if (caller.InstanceId != request.OriginInstanceId)
            return Task.FromResult<HostOutcome<WithdrawApprovedLibraryBatchReceipt>>(new HostOutcome<WithdrawApprovedLibraryBatchReceipt>.Forbidden());
        if (operation == Guid.Empty || request.BatchId == Guid.Empty || request.BatchRevision <= 0 || request.ReviewId == Guid.Empty)
            return Task.FromResult<HostOutcome<WithdrawApprovedLibraryBatchReceipt>>(new HostOutcome<WithdrawApprovedLibraryBatchReceipt>.Rejected("invalid-library-batch-withdrawal"));
        return store.WithdrawBatchAsync(library, caller, operation, request, token);
    }
}

/// <summary>Owns authoritative storage for one approved batch and its occupation of the entry operation IDs.</summary>
internal interface IApprovedLibraryBatchStore
{
    Task<HostOutcome<ApprovedLibraryBatchStatus>> SubmitBatchAsync(Guid library, AuthenticatedHostInstance caller, ApproveLibraryBatchRequest request,
        CancellationToken token);
    Task<HostOutcome<ApprovedLibraryBatchStatus>> ReadBatchAsync(Guid library, AuthenticatedHostInstance caller, Guid operation, CancellationToken token);
    Task<HostOutcome<WithdrawApprovedLibraryBatchReceipt>> WithdrawBatchAsync(Guid library, AuthenticatedHostInstance caller, Guid operation,
        WithdrawApprovedLibraryBatchRequest request, CancellationToken token);
}
