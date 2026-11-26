using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Distinguishes organization, catalog removal, and catalog plus audio removal.</summary>
/// <remarks>2026-11-24: Preserve the exact reviewed STB18 mode for FR-BEETS-001#ac-edit.</remarks>
public enum LibraryMaintenanceKind { Unspecified, Organize, RemoveCatalog, RemoveAudio }

/// <summary>Requests a preview for a fixed catalog revision and ordered track selection.</summary>
public sealed record LibraryMaintenancePreviewRequest(LibraryMaintenanceKind Kind, long CatalogRevision, ImmutableArray<Guid> TrackIds);

/// <summary>Identifies the exact source and destination for one selected track.</summary>
public sealed record MaintenancePathChange(Guid TrackId, string Source, string Destination);

/// <summary>Returns the effect and authorization identity the originating desktop must confirm.</summary>
/// <remarks>2026-11-24: Bind previewed organization and separately authorized audio deletion for FR-BEETS-001#ac-edit.</remarks>
public sealed record LibraryMaintenancePlan(Guid PlanId, Guid LibraryId, LibraryMaintenanceKind Kind, long CatalogRevision,
    ImmutableArray<Guid> TrackIds, ImmutableArray<MaintenancePathChange> Moves, ImmutableArray<string> AudioPaths);

/// <summary>Confirms one exact plan and supplies explicit authorization only for its audio removal mode.</summary>
public sealed record ConfirmLibraryMaintenanceRequest(Guid OriginInstanceId, Guid OperationId, LibraryMaintenanceKind Kind,
    long ExpectedCatalogRevision, bool AudioDeletionApproved = false);

/// <summary>Returns the stable job admitted for one reviewed plan.</summary>
public sealed record LibraryMaintenanceReceipt(Guid OperationId, Guid JobId, Guid LibraryId, long Revision);

/// <summary>Addresses maintenance previews and confirmations under the registered library.</summary>
public static class LibraryMaintenanceRoutes
{
    public const string PreviewPattern = "/api/v1/libraries/{libraryId:guid}/maintenance/previews";
    public const string ConfirmPattern = PreviewPattern + "/{planId:guid}/confirm";

    /// <summary>Builds the scoped preview path.</summary>
    public static string PreviewPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/maintenance/previews";

    /// <summary>Builds the confirmation path for the displayed plan.</summary>
    public static string ConfirmPath(Guid libraryId, Guid planId) => $"{PreviewPath(libraryId)}/{planId:D}/confirm";
}
