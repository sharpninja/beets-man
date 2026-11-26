using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Represents the reviewed effects the desktop may send only after explicit approval.</summary>
/// <remarks>2026-11-24: Preserve batch intent for FR-LIBRARY-002#ac-batch-review and ac-batch-commit.</remarks>
[JsonDerivedType(typeof(ApprovedBatchChange.TrackTags), "track-tags")]
[JsonDerivedType(typeof(ApprovedBatchChange.Import), "import")]
[JsonDerivedType(typeof(ApprovedBatchChange.Maintenance), "maintenance")]
[JsonDerivedType(typeof(ApprovedBatchChange.Playlist), "playlist")]
public abstract record ApprovedBatchChange
{
    private ApprovedBatchChange() { }

    /// <summary>Preserves the exact before values and reviewed patch at the selected track revision.</summary>
    public sealed record TrackTags(Guid TrackId, long ExpectedRevision, ImmutableSortedDictionary<string, string> Before,
        ImmutableSortedDictionary<string, string?> Changes) : ApprovedBatchChange;

    /// <summary>Describes the ordered private copies without disclosing original paths.</summary>
    public sealed record Import(ImmutableArray<ApprovedBatchImportFile> Files) : ApprovedBatchChange;

    /// <summary>Preserves the host plan, its effects, and the separate authorization for audio deletion.</summary>
    public sealed record Maintenance(LibraryMaintenancePlan Plan, bool AudioDeletionApproved) : ApprovedBatchChange;

    /// <summary>Retains the originating draft or queue and the playback order approved in the batch.</summary>
    public sealed record Playlist(PlaylistSaveSource Source, long SourceRevision, Guid? PlaylistId, long? ExpectedRevision,
        string Name, ImmutableArray<PlaylistEntryData> Entries) : ApprovedBatchChange;
}

/// <summary>Identifies one reviewed private file and its verified manifest values.</summary>
public sealed record ApprovedBatchImportFile(Guid FileId, ImportUploadFile Manifest);

/// <summary>Pins one entry's identity and position in the approved batch.</summary>
public sealed record ApprovedBatchEntry(Guid OperationId, int Position, ApprovedBatchChange Change);

/// <summary>Submits one exact ordered review and its stable approval operation.</summary>
public sealed record ApproveLibraryBatchRequest(Guid OriginInstanceId, Guid OperationId, Guid BatchId, long BatchRevision, Guid ReviewId,
    ImmutableArray<ApprovedBatchEntry> Entries);

/// <summary>Distinguishes journaling the approval from publication of its complete effects.</summary>
/// <remarks>2026-11-24: Report native execution and pre-write rejection for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-25: Preserve pre-write withdrawal on replay for FR-LIBRARY-002#ac-batch-revert.</remarks>
public enum BatchPublicationPhase { Unspecified, Accepted, Published, Running, Rejected, Withdrawn }

/// <summary>Identifies a frozen approval to withdraw without resubmitting its reviewed changes.</summary>
/// <remarks>2026-11-25: Withdraw only the exact originating review for FR-LIBRARY-002#ac-batch-revert.</remarks>
public sealed record WithdrawApprovedLibraryBatchRequest(Guid OriginInstanceId, Guid BatchId, long BatchRevision, Guid ReviewId);

/// <summary>Confirms the host has durably prevented this approval from starting.</summary>
public sealed record WithdrawApprovedLibraryBatchReceipt(Guid LibraryId, Guid OriginInstanceId, Guid ApprovalOperationId, Guid BatchId,
    long BatchRevision, Guid ReviewId);

/// <summary>Relates a published playlist to the originating entry so local state can bind the authoritative version.</summary>
public sealed record BatchPublishedPlaylist(Guid EntryOperationId, Guid PlaylistId, long PlaylistRevision);

/// <summary>Returns the stable batch identity, its phase, and any committed catalog revision.</summary>
public sealed record ApprovedLibraryBatchStatus(Guid LibraryId, Guid OriginInstanceId, Guid OperationId, Guid BatchId, long BatchRevision,
    Guid ReviewId, BatchPublicationPhase Phase, long? CatalogRevision, ImmutableArray<BatchPublishedPlaylist> Playlists,
    int ImportPartCount = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<ApprovedBatchImportPartReceipt> TransferredImports = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FailureCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<ApprovedBatchImportReview> ImportReviews = default);

/// <summary>Exposes an import entry's public review and any explicit decision at its recorded revision.</summary>
/// <remarks>2026-11-24: Carry guided choices in batch status for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
public sealed record ApprovedBatchImportReview(int Position, Guid EntryOperationId, long Revision, ImportAlbumReview Album,
    ImportChoiceKind? Choice = null, Guid? MatchId = null);

/// <summary>Chooses an action against one immutable import entry and the displayed revision.</summary>
public sealed record ChooseApprovedBatchImportRequest(Guid OriginInstanceId, Guid OperationId, Guid EntryOperationId, Guid ReviewItemId,
    long ExpectedRevision, ImportChoiceKind Choice, Guid? MatchId = null);

/// <summary>Retains the original choice and exact scope for replay after an interrupted response.</summary>
public sealed record ApprovedBatchImportChoiceReceipt(Guid LibraryId, Guid OriginInstanceId, Guid ApprovalOperationId, Guid EntryOperationId,
    Guid OperationId, int Position, long Revision, Guid ReviewItemId, ImportChoiceKind Choice, Guid? MatchId = null);

/// <summary>Identifies a verified file under one exact approved batch entry and ordinal.</summary>
/// <remarks>2026-11-24: Resume only unacknowledged copies for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed record ApprovedBatchImportPartReceipt(Guid LibraryId, Guid OriginInstanceId, Guid ApprovalOperationId, Guid EntryOperationId,
    int Position, int Index, Guid FileId);

/// <summary>Addresses the authenticated whole-batch submission and status routes.</summary>
/// <remarks>2026-11-24: Route FR-LIBRARY-002#ac-batch-commit as one approved operation.</remarks>
public static class ApprovedLibraryBatchRoutes
{
    public const string ApprovePattern = "/api/v1/libraries/{libraryId:guid}/approved-batches";
    public const string StatusPattern = ApprovePattern + "/{operationId:guid}";
    public const string WithdrawPattern = StatusPattern + "/withdraw";
    public const string ImportPartPattern = StatusPattern + "/entries/{position:int}/imports/{index:int}/{fileId:guid}";
    public const string ImportChoicePattern = StatusPattern + "/entries/{position:int}/import-choice";

    /// <summary>Addresses the registered library's batch submission.</summary>
    public static string ApprovePath(Guid library) => $"api/v1/libraries/{library:D}/approved-batches";

    /// <summary>Addresses the requesting desktop's original operation.</summary>
    public static string StatusPath(Guid library, Guid operation) => $"{ApprovePath(library)}/{operation:D}";

    /// <summary>Addresses withdrawal of the authenticated desktop's exact approval.</summary>
    public static string WithdrawPath(Guid library, Guid operation) => $"{StatusPath(library, operation)}/withdraw";

    /// <summary>Addresses one file from the immutable approved entry.</summary>
    public static string ImportPartPath(Guid library, Guid approval, int position, int index, Guid file) =>
        FormattableString.Invariant($"{StatusPath(library, approval)}/entries/{position}/imports/{index}/{file:D}");

    /// <summary>Addresses the explicit decision for one verified batch import entry.</summary>
    public static string ImportChoicePath(Guid library, Guid approval, int position) =>
        FormattableString.Invariant($"{StatusPath(library, approval)}/entries/{position}/import-choice");
}
