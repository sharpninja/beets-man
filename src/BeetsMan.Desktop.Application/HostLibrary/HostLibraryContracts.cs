using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.HostLibrary;

/// <summary>Describes the editable sections of a host library configuration.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-BEETS-001#ac-open, ac-import, ac-edit, and ac-recover.</remarks>
/// <remarks>2026-11-25: Keep the provisioned host YAML separate from the native catalog for FR-BEETS-001#ac-open.</remarks>
public sealed record HostLibrarySettings(string CatalogPath, string SourceMusicRoot, string NamingTemplate,
    ImmutableDictionary<string, string> ImportDefaults, ImmutableArray<string> EnabledPluginIds, string? ConfigurationPath = null);

/// <summary>Returns current library readiness and configuration revision.</summary>
public sealed record HostLibraryState(Guid LibraryId, long Revision, bool IsInitialized, bool IsOnline, bool CanEdit);

/// <summary>Contains supported host configuration and its revision.</summary>
/// <remarks>2026-11-25: Carry the approved plugin choices alongside enabled plugins for FR-BEETS-001#ac-open.</remarks>
public sealed record HostLibraryConfiguration(Guid LibraryId, long Revision, HostLibrarySettings Settings, bool CanEdit)
{
    public ImmutableArray<string> AvailablePluginIds { get; init; } = [];
}

/// <summary>Contains an opaque local upload selection token and display names.</summary>
public sealed record ImportSourceSelection(Guid SelectionToken, ImmutableArray<string> Names);

/// <summary>Identifies an accepted host job or the local stage that replaced the host submission.</summary>
/// <remarks>2026-11-24: Return a distinct local batch receipt for FR-LIBRARY-002#ac-batch-local.</remarks>
public sealed record LibraryJobReceipt(Guid JobId, Guid LibraryId, long Revision, LibraryBatchStageReceipt? LocalBatch = null);

/// <summary>Identifies the current job phase.</summary>
public enum LibraryJobPhase { Accepted, Running, ReviewRequired, Published, Failed, Skipped }

/// <summary>Represents a reviewed metadata match.</summary>
public sealed record ImportMatch(Guid MatchId, string Title, string Artist, ImportMatchDetails? Details = null);

/// <summary>Supplies the album edition, aligned title preview, and eligibility for approval.</summary>
/// <remarks>2026-11-24: Bind the SCR37 comparison data for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportMatchDetails(string? ReleaseDate, string? Country, int? ReleaseTrackCount, bool CanApply,
    ImmutableArray<ImportTrackTitleComparison> Tracks);

/// <summary>Pairs a staged track's original title with the native autofill suggestion.</summary>
public sealed record ImportTrackTitleComparison(int SourceIndex, string OriginalTitle, string? ProposedTitle);

/// <summary>Contains the accepted choices for one import review item.</summary>
/// <remarks>2026-11-24: Carry the ordered album source names for FR-BEETS-001#ac-import.</remarks>
public sealed record ImportReviewItem(Guid ItemId, ImmutableArray<ImportMatch> Matches, ImmutableArray<string> SourceNames = default);

/// <summary>Returns current host job status and any publication revision.</summary>
public sealed record LibraryJobStatus(Guid JobId, Guid LibraryId, long Revision, LibraryJobPhase Phase, ImportReviewItem? Review = null, long? CatalogRevision = null, string? FailureCode = null, bool Retryable = false);

/// <summary>Specifies the operator choice for one import review item.</summary>
public abstract record ImportAction
{
    public sealed record UseMatch(Guid MatchId) : ImportAction;
    public sealed record KeepTags : ImportAction;
    public sealed record Skip : ImportAction;
}

/// <summary>Returns a track's editable tags at a catalog revision.</summary>
/// <remarks>2026-11-24: Identify the overlay being shown so the editor can restore its baseline for FR-LIBRARY-002#ac-batch-revert.</remarks>
public sealed record TrackTagSnapshot(Guid TrackId, long CatalogRevision, ImmutableDictionary<string, string> Tags, Guid? OverlayBatchId = null);

/// <summary>Reads a track's authoritative last-seen tags from the desktop catalog projection.</summary>
/// <remarks>2026-11-24: Provide the offline edit baseline for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public interface ICachedTrackTags
{
    Task<Outcome<TrackTagSnapshot>> ReadCachedTrackTagsAsync(Guid trackId, CancellationToken token);
}

/// <summary>Describes the source and destination for an identified track.</summary>
/// <remarks>2026-11-24: Retain the track identity for FR-BEETS-001#ac-edit and FR-LIBRARY-002#ac-batch-review.</remarks>
public sealed record FileMove(string Source, string Destination, Guid TrackId = default);

/// <summary>Returns the ordered selection, moves, and revision a confirmation will authorize.</summary>
/// <remarks>2026-11-24: Retain selected tracks that already use their destination for FR-BEETS-001#ac-edit and FR-LIBRARY-002#ac-batch-review.</remarks>
public sealed record OrganizePlan(Guid PlanId, Guid LibraryId, long CatalogRevision, ImmutableArray<FileMove> Moves,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<Guid> TrackIds = default);

/// <summary>Distinguishes catalog-only removal from separately approved audio deletion.</summary>
public enum RemovalMode { CatalogOnly, CatalogAndAudio }

/// <summary>Returns the exact records and any audio paths affected by removal.</summary>
public sealed record RemovalPlan(Guid PlanId, Guid LibraryId, long CatalogRevision, RemovalMode Mode, ImmutableArray<Guid> TrackIds, ImmutableArray<string> AudioPaths);

/// <summary>Identifies an explicit approval for the selected plan and its removal mode.</summary>
public sealed record RemovalApproval(Guid PlanId, RemovalMode Mode);

/// <summary>Reads the current host library and its readiness.</summary>
public sealed record GetHostLibraryState(Guid LibraryId) : IQuery<HostLibraryState>;

/// <summary>Initializes the library from approved configuration.</summary>
public sealed record InitializeHostLibrary(Guid LibraryId, HostLibrarySettings ApprovedSettings, long ExpectedRevision) : ICommand<HostLibraryState>;

/// <summary>Reads the current supported host settings.</summary>
public sealed record GetHostLibraryConfiguration(Guid LibraryId) : IQuery<HostLibraryConfiguration>;

/// <summary>Writes supported host settings at an expected revision.</summary>
public sealed record SaveHostLibraryConfiguration(Guid LibraryId, HostLibrarySettings Edits, long ExpectedRevision) : ICommand<HostLibraryConfiguration>;

/// <summary>Obtains a token for user-selected local import sources.</summary>
public sealed record SelectImportSources(Guid OwnerId) : ICommand<ImportSourceSelection>;

/// <summary>Stages uploaded copies and submits one accepted import job.</summary>
public sealed record UploadImportSources(Guid LibraryId, Guid SourceSelectionToken) : ICommand<LibraryJobReceipt>;

/// <summary>Starts import from a verified completed upload.</summary>
public sealed record StartImport(Guid LibraryId, Guid UploadId) : ICommand<LibraryJobReceipt>;

/// <summary>Submits the exact review choice at the displayed job revision.</summary>
public sealed record ChooseImportAction(Guid JobId, Guid ReviewItemId, ImportAction Choice, long ExpectedRevision) : ICommand<LibraryJobReceipt>;

/// <summary>Reads an independent accepted job snapshot.</summary>
public sealed record GetLibraryJob(Guid JobId) : IQuery<LibraryJobStatus>;

/// <summary>Reconciles and retries the identified job.</summary>
public sealed record RetryLibraryJob(Guid JobId, long ExpectedRevision) : ICommand<LibraryJobReceipt>;

/// <summary>Reads editable metadata for one track.</summary>
public sealed record GetTrackTags(Guid TrackId) : IQuery<TrackTagSnapshot>;

/// <summary>Submits an authorized tag mutation against the catalog revision.</summary>
public sealed record EditTrackTags(Guid TrackId, ImmutableDictionary<string, string?> TagChanges, long ExpectedCatalogRevision) : ICommand<LibraryJobReceipt>;

/// <summary>Previews the file moves for the selected tracks.</summary>
public sealed record GetOrganizePlan(Guid LibraryId, ImmutableArray<Guid> TrackIds, long CatalogRevision) : IQuery<OrganizePlan>;

/// <summary>Accepts the previewed organization plan and supplies its displayed contents for local staging.</summary>
/// <remarks>2026-11-24: Stage the displayed effects for FR-LIBRARY-002#ac-batch-local and ac-batch-review.</remarks>
public sealed record OrganizeTracks(Guid PlanId, long ExpectedCatalogRevision, OrganizePlan? ReviewedPlan = null) : ICommand<LibraryJobReceipt>;

/// <summary>Previews removal for the explicitly selected mode.</summary>
public sealed record GetRemovalPlan(Guid LibraryId, ImmutableArray<Guid> TrackIds, RemovalMode Mode, long CatalogRevision) : IQuery<RemovalPlan>;

/// <summary>Accepts the confirmed mode and plan and supplies its displayed contents for local staging.</summary>
/// <remarks>2026-11-24: Keep the explicit removal mode in the review for FR-LIBRARY-002#ac-batch-local and ac-batch-review.</remarks>
public sealed record RemoveTracks(Guid PlanId, long ExpectedCatalogRevision, RemovalApproval Approval,
    RemovalPlan? ReviewedPlan = null) : ICommand<LibraryJobReceipt>;
