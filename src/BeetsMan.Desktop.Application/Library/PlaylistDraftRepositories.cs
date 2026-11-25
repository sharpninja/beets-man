using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Identifies a durable pending operation across retries and application restarts.</summary>
/// <remarks>2026-11-24: Materialize local draft persistence for FR-RECORDS-001#ac-offline and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
public readonly record struct OperationKey(Guid OriginInstanceId, Guid OperationId);

/// <summary>Distinguishes a pending save, a completed receipt, and a conflict requiring resolution.</summary>
public enum PendingPlaylistSaveStatus { Pending, Accepted, Conflicted, Rejected }

/// <summary>Stores the exact ordered playlist and expected versions for a replayable save operation.</summary>
/// <remarks>2026-11-24: Record the source queue for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed record PendingPlaylistSave(OperationKey Key, Guid DraftId, long DraftRevision, PlaylistVersion? BasePlaylist,
    string Name, ImmutableArray<PlaylistEntry> Entries, PendingPlaylistSaveStatus Status, PlaylistSavedReceipt? Receipt = null,
    string? ConflictCode = null, long? ActualHostRevision = null, ImmutableArray<ValidationIssue> Issues = default,
    QueuePlaylistSaveSource? QueueSource = null);

/// <summary>Identifies the originating playback queue and the prior save whose revision must be observed.</summary>
/// <remarks>2026-11-24: Preserve the intent and dependency chain for FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-offline.</remarks>
public sealed record QueuePlaylistSaveSource(Guid SessionId, long QueueRevision, string IntentHash, OperationKey? Predecessor,
    bool ExpectedRevisionPinned);

/// <summary>Reads local playlist drafts outside the writer callback.</summary>
public interface IPlaylistDraftReader
{
    /// <summary>Reads one detached draft snapshot by its stable identifier.</summary>
    Task<Outcome<PlaylistDraft?>> ReadDraftAsync(Guid draftId, CancellationToken cancellationToken);
}

/// <summary>Accesses ordered local draft state inside a write transaction.</summary>
public interface IPlaylistDraftRepository
{
    /// <summary>Looks up an existing draft.</summary>
    PlaylistDraft? Find(Guid draftId);

    /// <summary>Inserts a new draft.</summary>
    void Add(PlaylistDraft draft);

    /// <summary>Replaces the draft only when its stored revision matches.</summary>
    bool TryReplace(PlaylistDraft draft, long expectedRevision);
}

/// <summary>Accesses playlist operations and their receipts inside the same write transaction.</summary>
public interface IPendingPlaylistSaveRepository
{
    /// <summary>Resolves an attempted save by its stable operation key.</summary>
    PendingPlaylistSave? Find(OperationKey key);

    /// <summary>Records a new pending save and its replay payload.</summary>
    void Add(PendingPlaylistSave operation);

    /// <summary>Stores an observed outcome for an existing operation.</summary>
    bool TryReplace(PendingPlaylistSave operation, PendingPlaylistSaveStatus expected);
}

/// <summary>Specifies the collections participating in one local playlist write.</summary>
public interface IPlaylistDraftWriteRepositories
{
    /// <summary>Gets the local draft repository for this transaction.</summary>
    IPlaylistDraftRepository Drafts { get; }

    /// <summary>Gets the pending operation repository for this transaction.</summary>
    IPendingPlaylistSaveRepository Operations { get; }
}
