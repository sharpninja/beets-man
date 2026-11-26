using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Stores the local toggle, active review, and any approved publication awaiting resolution.</summary>
/// <remarks>2026-11-24: Persist restart-safe batch control state for FR-LIBRARY-002#ac-batch-review and ac-batch-toggle-off.</remarks>
public sealed record LibraryBatchRecord(Guid? BatchId, long Revision, int ChangeCount, Guid? ReviewId = null, LibraryBatchSeal? Seal = null);

/// <summary>Retains the number and identity of the exact approval admitted for publication.</summary>
public sealed record LibraryBatchSeal(Guid OperationId, CommitLibraryBatch Command);

/// <summary>Pins a staged operation fingerprint after its private payload has been discarded.</summary>
/// <remarks>2026-11-24: Preserve the result of a committed queue save for FR-LIBRARY-002#ac-batch-commit and FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed record LibraryBatchStageRecord(LibraryBatchStageReceipt Receipt, string Fingerprint, PublishedBatchQueueSave? QueuePublication = null);

/// <summary>Connects a committed queue save to its original session, revision and command intent.</summary>
public sealed record PublishedBatchQueueSave(QueuePlaylistSaveSource Source, PlaylistVersion Version);

/// <summary>Records the approved command and original authoritative receipt after finalized publication.</summary>
public sealed record LibraryBatchCommitRecord(LibraryBatchSeal Seal, LibraryBatchCommitReceipt Receipt);

/// <summary>Accesses the mode, ordered entries, and operation receipts within one desktop transaction.</summary>
/// <remarks>2026-11-24: Bind batch entities to TR-LIBRARY-ARCH-002 and FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public interface ILibraryBatchRepository
{
    LibraryBatchRecord Read(LibraryBatchScope scope);
    void Save(LibraryBatchScope scope, LibraryBatchRecord record);
    ImmutableArray<LibraryBatchEntry> ReadEntries(LibraryBatchScope scope, Guid batchId);
    LibraryBatchStageRecord? FindStage(LibraryBatchScope scope, Guid operationId);
    LibraryBatchCommitRecord? FindCommit(LibraryBatchScope scope, Guid operationId);
    LibraryBatchEntry? FindEntry(LibraryBatchScope scope, Guid batchId, int position);
    bool IsDirect(LibraryBatchScope scope, Guid operationId);
    void RecordDirect(LibraryBatchScope scope, Guid operationId);
    void AddStage(LibraryBatchScope scope, LibraryBatchStageRecord stage, LibraryBatchEntry entry);
    void RecordCommit(LibraryBatchScope scope, LibraryBatchCommitRecord committed);
    /// <summary>Attaches the authoritative playlist revision to a staged queue command in the batch commit transaction.</summary>
    void RecordPublishedQueueSave(LibraryBatchScope scope, Guid operationId, PublishedBatchQueueSave published);
    void RemoveEntries(LibraryBatchScope scope, Guid batchId);
    void ClaimImportCopies(LibraryBatchScope scope, Guid operationId);
    ImmutableArray<Guid> ImportCopyClaims(LibraryBatchScope scope);
    void ReleaseImportCopies(LibraryBatchScope scope, Guid operationId);
    /// <summary>Binds the final published version to its existing named playlist draft, preserving newer draft text and entries.</summary>
    void BindPublishedDraft(LibraryBatchChange.Playlist staged, PlaylistVersion published);
}

/// <summary>Owns verified import copies under the originating desktop's private staging root.</summary>
/// <remarks>2026-11-24: Retain the reviewed bytes across restart and remove only app-owned copies for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public interface ILibraryBatchImportFiles
{
    Task<Outcome<ImmutableArray<LibraryBatchImportFile>>> CopyAsync(LibraryBatchScope scope, Guid operationId,
        ImmutableArray<string> sources, CancellationToken token);

    /// <summary>Rechecks the approved bytes and returns a read-only stream positioned at the beginning.</summary>
    Task<Outcome<Stream>> OpenVerifiedAsync(LibraryBatchScope scope, Guid operationId, LibraryBatchImportFile file, CancellationToken token);

    /// <summary>Discards only the private copies owned by this operation. Missing copies count as released.</summary>
    Task<Outcome<bool>> DiscardAsync(LibraryBatchScope scope, Guid operationId, CancellationToken token);
}

/// <summary>Submits the entire approved batch as one operation to the authoritative publisher.</summary>
/// <remarks>2026-11-24: Withhold every edit until exact approval for FR-LIBRARY-002#ac-batch-commit.</remarks>
public interface IApprovedLibraryBatchPublisher
{
    /// <summary>Returns the original receipt on replay. Invalid or conflict results assert that publication had no effects.</summary>
    Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId, CancellationToken token);
}

/// <summary>Returns durable confirmation that the exact approval can no longer start on the host.</summary>
/// <remarks>2026-11-25: Fence uncertain submissions for FR-LIBRARY-002#ac-batch-revert.</remarks>
public interface IApprovedLibraryBatchWithdrawal
{
    Task<Outcome<LibraryBatchWithdrawalReceipt>> WithdrawAsync(LibraryBatchScope scope, LibraryBatchApproval approval, Guid operationId,
        CancellationToken token);
}

/// <summary>Identifies the authenticated approval whose effects are permanently withdrawn.</summary>
public sealed record LibraryBatchWithdrawalReceipt(LibraryBatchScope Scope, LibraryBatchApproval Approval, Guid OperationId);

/// <summary>Reads and records guided choices for the original, scoped batch approval.</summary>
/// <remarks>2026-11-24: Keep hosted choices tied to their local seal for FR-LIBRARY-002#ac-batch-commit.</remarks>
public interface IApprovedBatchImportReviewGateway
{
    Task<Outcome<LibraryBatchImportReviews>> ReadImportReviewsAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid approval,
        CancellationToken token);
    Task<Outcome<LibraryBatchImportChoiceReceipt>> ChooseImportAsync(LibraryBatchScope scope, Guid approval, ChooseLibraryBatchImportAction choice,
        Guid operation, CancellationToken token);
}
