using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Describes one selected source by its display name, byte size, and SHA-256 digest.</summary>
/// <remarks>2026-11-24: Pin exact staged copies for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportUploadFile(string Name, long Size, string Sha256);

/// <summary>Reserves the ordered file manifest for a registered desktop.</summary>
public sealed record BeginImportUploadRequest(Guid OriginInstanceId, Guid UploadId, ImmutableArray<ImportUploadFile> Files);

/// <summary>Confirms an exact reserved manifest.</summary>
public sealed record ImportUploadReceipt(Guid UploadId);

/// <summary>Confirms that a numbered file was copied in full and verified.</summary>
public sealed record ImportUploadPartReceipt(Guid UploadId, int Index);

/// <summary>Submits one completed upload for guided import.</summary>
public sealed record StartImportRequest(Guid OriginInstanceId, Guid OperationId, Guid UploadId);

/// <summary>Returns the original durable acceptance of a guided import job.</summary>
public sealed record StartImportReceipt(Guid OperationId, Guid JobId, Guid LibraryId, long Revision);

/// <summary>Identifies the phase of an authorized guided import job.</summary>
public enum ImportJobPhase { Accepted, Running, ReviewRequired, Published, Failed, Skipped }

/// <summary>Identifies an offered album match without exposing the beets provider key.</summary>
/// <remarks>2026-11-24: Offer explicit album choices for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportReviewMatch(Guid MatchId, string Title, string Artist, ImportMatchComparison? Comparison = null);

/// <summary>Summarizes the proposed edition and its comparison against the staged album in source order.</summary>
/// <remarks>2026-11-24: Expose the tag comparison described in SCR37 for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportMatchComparison(string? ReleaseDate, string? Country, int? ReleaseTrackCount,
    bool CanApply, ImmutableArray<ImportTrackComparison> Tracks);

/// <summary>Describes the original title and proposed title for a staged source track.</summary>
public sealed record ImportTrackComparison(int SourceIndex, string OriginalTitle, string? ProposedTitle);

/// <summary>Describes the one album review for the entire ordered upload.</summary>
public sealed record ImportAlbumReview(Guid ItemId, ImmutableArray<string> SourceNames, ImmutableArray<ImportReviewMatch> Matches);

/// <summary>Identifies the operator's decision for the offered album.</summary>
public enum ImportChoiceKind { Unspecified, UseMatch, KeepTags, Skip }

/// <summary>Submits a selected choice against the exact review revision.</summary>
/// <remarks>2026-11-24: Persist the immutable review decision and operation receipt for FR-BEETS-001#ac-recover.</remarks>
public sealed record ChooseImportRequest(Guid OriginInstanceId, Guid OperationId, Guid ReviewItemId,
    long ExpectedRevision, ImportChoiceKind Choice, Guid? MatchId = null);

/// <summary>Returns the original durable acceptance of an album review choice.</summary>
public sealed record ChooseImportReceipt(Guid OperationId, Guid JobId, Guid LibraryId, long Revision);

/// <summary>Resumes a failed import at the displayed revision while preserving its fenced native attempt.</summary>
/// <remarks>2026-11-24: Route authenticated job recovery for FR-BEETS-001#ac-recover.</remarks>
public sealed record RetryImportRequest(Guid OriginInstanceId, Guid OperationId, long ExpectedRevision);

/// <summary>Confirms that the original guided import has been scheduled for recovery.</summary>
public sealed record RetryImportReceipt(Guid OperationId, Guid JobId, Guid LibraryId, long Revision);

/// <summary>Returns the current guidance and publication status of the selected job.</summary>
/// <remarks>2026-11-24: Expose the persisted review when it is awaiting a choice for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportJobSnapshot(Guid JobId, Guid LibraryId, long Revision, ImportJobPhase Phase,
    long? CatalogRevision = null, string? FailureCode = null, bool Retryable = false, ImportAlbumReview? Review = null);

/// <summary>Defines the private, versioned guided import intake routes.</summary>
public static class ImportIntakeRoutes
{
    public const string BeginPattern = "/api/v1/libraries/{libraryId:guid}/import-uploads";
    public const string PartPattern = BeginPattern + "/{uploadId:guid}/files/{index:int}";
    public const string StartPattern = "/api/v1/libraries/{libraryId:guid}/library-jobs/import";
    public const string JobPattern = "/api/v1/libraries/{libraryId:guid}/library-jobs/{jobId:guid}";
    public const string ChoicePattern = JobPattern + "/import-choice";
    public const string RetryPattern = JobPattern + "/retry";

    /// <summary>Builds the manifest submission path for the registered library.</summary>
    public static string BeginPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/import-uploads";

    /// <summary>Builds the streaming endpoint for the selected file ordinal.</summary>
    public static string PartPath(Guid libraryId, Guid uploadId, int index) => $"{BeginPath(libraryId)}/{uploadId:D}/files/{index}";

    /// <summary>Builds the acceptance endpoint for a completed upload.</summary>
    public static string StartPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/library-jobs/import";

    /// <summary>Builds the authorized job lookup path.</summary>
    public static string JobPath(Guid libraryId, Guid jobId) => $"api/v1/libraries/{libraryId:D}/library-jobs/{jobId:D}";

    /// <summary>Builds the authenticated album approval path.</summary>
    public static string ChoicePath(Guid libraryId, Guid jobId) => $"{JobPath(libraryId, jobId)}/import-choice";

    /// <summary>Builds the authenticated guided import retry path.</summary>
    public static string RetryPath(Guid libraryId, Guid jobId) => $"{JobPath(libraryId, jobId)}/retry";
}
