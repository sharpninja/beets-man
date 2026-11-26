using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Identifies the local editable library and the desktop that owns its unpublished changes.</summary>
/// <remarks>2026-11-24: Scope FR-LIBRARY-002#ac-batch-local to the originating desktop and registered library.</remarks>
public readonly record struct LibraryBatchScope(Guid LibraryId, Guid OriginInstanceId);

/// <summary>Returns the current toggle, revision, change count, and durable commit state.</summary>
/// <remarks>2026-11-24: Expose FR-LIBRARY-002#ac-batch-toggle-off to the application.</remarks>
public sealed record LibraryBatchMode(Guid LibraryId, Guid? BatchId, long Revision, int ChangeCount, bool IsCommitting, bool ImportCleanupPending = false)
{
    public bool IsEnabled => BatchId.HasValue;
}

/// <summary>Pins a batch and the revision the caller saw.</summary>
public readonly record struct LibraryBatchVersion(Guid BatchId, long Revision);

/// <summary>Names the exact revision that was presented to the operator for approval.</summary>
/// <remarks>2026-11-24: Bind commit to the review for FR-LIBRARY-002#ac-batch-review and ac-batch-commit.</remarks>
public readonly record struct LibraryBatchApproval(LibraryBatchVersion Version, Guid ReviewId);

/// <summary>Enumerates the typed library edits accepted at the local staging boundary.</summary>
/// <remarks>2026-11-24: Represent file, metadata, and playlist intents for TR-LIBRARY-ARCH-002 and FR-LIBRARY-002#ac-batch-local.</remarks>
[JsonDerivedType(typeof(LibraryBatchChange.TrackTags), "track-tags")]
[JsonDerivedType(typeof(LibraryBatchChange.Import), "import")]
[JsonDerivedType(typeof(LibraryBatchChange.Organize), "organize")]
[JsonDerivedType(typeof(LibraryBatchChange.Remove), "remove")]
[JsonDerivedType(typeof(LibraryBatchChange.Playlist), "playlist")]
public abstract record LibraryBatchChange
{
    private LibraryBatchChange() { }

    /// <summary>Retains the selected track and exact before and after fields for review.</summary>
    public sealed record TrackTags(Guid TrackId, long ExpectedRevision, ImmutableSortedDictionary<string, string> Before,
        ImmutableSortedDictionary<string, string?> Changes) : LibraryBatchChange;

    /// <summary>Identifies an ordered set of source copies in the private desktop staging area.</summary>
    public sealed record Import(ImmutableArray<LibraryBatchImportFile> Files, Guid? SelectionToken = null) : LibraryBatchChange;

    /// <summary>Retains the exact paths and expected revision that were presented in the organize preview.</summary>
    public sealed record Organize(OrganizePlan Plan) : LibraryBatchChange;

    /// <summary>Retains the approved removal scope and whether it includes audio deletion.</summary>
    public sealed record Remove(RemovalPlan Plan, RemovalApproval Approval) : LibraryBatchChange;

    /// <summary>Retains the exact ordered playlist to save, optionally from a playback queue.</summary>
    public sealed record Playlist(Guid DraftId, long DraftRevision, PlaylistVersion? BasePlaylist, string Name,
        ImmutableArray<PlaylistEntry> Entries, QueuePlaylistSaveSource? QueueSource = null) : LibraryBatchChange;
}

/// <summary>Identifies the copied file by its private key and verified content, without exposing its original path.</summary>
public sealed record LibraryBatchImportFile(Guid FileId, string Name, long Size, string Sha256);

/// <summary>Places a change at its durable position in the operator's review.</summary>
public sealed record LibraryBatchEntry(Guid OperationId, int Position, LibraryBatchChange Change);

/// <summary>Lists the ordered changes presented for approval in one immutable review.</summary>
public sealed record LibraryBatchReview(LibraryBatchVersion Version, Guid? ReviewId, ImmutableArray<LibraryBatchEntry> Entries);

/// <summary>Confirms a library edit is staged only on the originating desktop.</summary>
public sealed record LibraryBatchStageReceipt(Guid OperationId, LibraryBatchVersion Version, int Position);

/// <summary>Confirms the host published the approved batch at its authoritative catalog revision.</summary>
/// <remarks>2026-11-24: Preserve playlist identities after approval for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed record LibraryBatchCommitReceipt(Guid LibraryId, LibraryBatchVersion Version, Guid OperationId, long CatalogRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<LibraryBatchPublishedPlaylist> Playlists = default);

/// <summary>Relates a reviewed entry to its authoritative playlist version.</summary>
public sealed record LibraryBatchPublishedPlaylist(Guid EntryOperationId, PlaylistVersion Version);

/// <summary>Reads the desktop's current batch-mode toggle and operation state.</summary>
public sealed record GetLibraryBatchMode : IQuery<LibraryBatchMode>;

/// <summary>Changes the toggle only at the observed revision.</summary>
public sealed record SetLibraryBatchMode(bool Enabled, long ExpectedRevision) : ICommand<LibraryBatchMode>;

/// <summary>Reads every pending change and captures the approval identity for that revision.</summary>
public sealed record GetLibraryBatchReview : IQuery<LibraryBatchReview>;

/// <summary>Reads the guided import reviews for the current sealed batch.</summary>
/// <remarks>2026-11-24: Surface private guided review before batch publication for FR-BEETS-001#ac-import and FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed record GetLibraryBatchImportReviews : IQuery<LibraryBatchImportReviews>;

/// <summary>Identifies the sealed approval and available import choices in review order.</summary>
public sealed record LibraryBatchImportReviews(LibraryBatchVersion Version, Guid ApprovalOperationId, ImmutableArray<LibraryBatchImportReview> Entries);

/// <summary>Combines the public album review and the explicit choice at its recorded revision.</summary>
public sealed record LibraryBatchImportReview(int Position, Guid EntryOperationId, long Revision, ImportReviewItem Album, ImportAction? Action);

/// <summary>Approves a guided action against the displayed batch, entry and revision.</summary>
public sealed record ChooseLibraryBatchImportAction(LibraryBatchVersion Version, int Position, Guid EntryOperationId, Guid ReviewItemId,
    long ExpectedRevision, ImportAction Action) : ICommand<LibraryBatchImportChoiceReceipt>;

/// <summary>Returns the stable decision operation and exact approved batch scope.</summary>
public sealed record LibraryBatchImportChoiceReceipt(LibraryBatchVersion Version, Guid ApprovalOperationId, int Position,
    Guid EntryOperationId, Guid OperationId, long Revision, ImportAction Action);

/// <summary>Approves one reviewed revision and selects the mode to use after publication.</summary>
public sealed record CommitLibraryBatch(LibraryBatchApproval Approval, bool KeepModeEnabled = true) : ICommand<LibraryBatchCommitReceipt>;

/// <summary>Resolves the original approved publication when its response was lost, including after restart.</summary>
/// <remarks>2026-11-24: Reuse the durable approval and operation for FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed record ResumeLibraryBatchCommit : ICommand<LibraryBatchCommitReceipt>;

/// <summary>Discards exactly the pending revision and selects the mode to use afterward.</summary>
public sealed record RevertLibraryBatch(LibraryBatchVersion Version, bool KeepModeEnabled = true) : ICommand<LibraryBatchMode>;
