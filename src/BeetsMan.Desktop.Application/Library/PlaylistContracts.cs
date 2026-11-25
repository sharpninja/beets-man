using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Identifies one occurrence of a track in a playlist.</summary>
/// <remarks>2026-11-24: Materialize editable playlist contracts for FR-RECORDS-001#ac-offline and FR-LIBRARY-001#ac-browse.</remarks>
public sealed record PlaylistEntry(Guid EntryId, Guid TrackId);

/// <summary>Returns the saved playlist version used as a draft's base.</summary>
public sealed record PlaylistVersion(Guid PlaylistId, long Revision);

/// <summary>Contains a draft and its exact ordered entry identities.</summary>
public sealed record PlaylistDraft(Guid DraftId, long Revision, string Name, ImmutableArray<PlaylistEntry> Entries, PlaylistVersion? BasePlaylist);

/// <summary>Identifies the committed playlist and corresponding local draft.</summary>
/// <remarks>2026-11-24: Carry the playback session on queue auto-save receipts for FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed record PlaylistSavedReceipt(Guid DraftId, Guid PlaylistId, long Revision, long? DraftRevision = null, Guid? QueueSessionId = null);

/// <summary>Specifies how a draft begins.</summary>
public abstract record PlaylistDraftSource
{
    public sealed record Empty : PlaylistDraftSource;
    public sealed record QueueSnapshot(Guid SessionId, long Revision, ImmutableArray<Guid> OrderedTrackIds) : PlaylistDraftSource;
    public sealed record SavedPlaylist(Guid PlaylistId, long ExpectedRevision) : PlaylistDraftSource;
}

/// <summary>Specifies an ordered edit of the draft.</summary>
public abstract record PlaylistDraftEdit
{
    public sealed record Append(ImmutableArray<Guid> OrderedTrackIds) : PlaylistDraftEdit;
    public sealed record Reorder(ImmutableArray<Guid> OrderedEntryIds) : PlaylistDraftEdit;
    public sealed record Remove(ImmutableArray<Guid> EntryIds) : PlaylistDraftEdit;
}

/// <summary>Loads a draft by its workspace identity.</summary>
public sealed record GetPlaylistDraft(Guid DraftId) : IQuery<PlaylistDraft>;

/// <summary>Starts a draft from a captured source.</summary>
public sealed record CreatePlaylistDraft(PlaylistDraftSource Source) : ICommand<PlaylistDraft>;

/// <summary>Applies an edit to one expected draft revision.</summary>
public sealed record EditPlaylistDraft(Guid DraftId, PlaylistDraftEdit Operation, long ExpectedRevision) : ICommand<PlaylistDraft>;

/// <summary>Persists a named playlist and returns the committed version.</summary>
public sealed record SavePlaylist(Guid DraftId, string Name, long ExpectedDraftRevision, long? ExpectedPlaylistRevision) : ICommand<PlaylistSavedReceipt>;
