using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Lists the operations supported by the active playback backend.</summary>
/// <remarks>2026-11-24: Define the shared player and queue contracts for FR-PLAYER-001#ac-transport, ac-mix, ac-fade and ac-queue-autosave.</remarks>
[Flags]
public enum PlaybackCapabilities { None = 0, Transport = 1, Seek = 2, Volume = 4, Mixer = 8, Equalizer = 16, Visualization = 32 }

/// <summary>Identifies one of the two independently cued decks.</summary>
public enum DeckId { A, B }

/// <summary>Describes the current transport state.</summary>
public enum TransportStatus { Stopped, Playing, Paused }

/// <summary>Identifies an explicit transport request.</summary>
public enum TransportAction { Play, Pause, Next, Previous, Stop }

/// <summary>Determines whether enqueueing starts playback of the selection.</summary>
public enum EnqueueMode { Append, PlayNow }

/// <summary>Specifies the queue repetition policy.</summary>
public enum RepeatMode { Off, All, One }

/// <summary>Distinguishes queue-only edits from the outcome of a requested playlist save.</summary>
public enum QueueSaveStatus { NotRequested, Saved, Pending, Conflict, Failed }

/// <summary>Contains one occurrence of a track in the active queue.</summary>
public sealed record QueueEntry(Guid EntryId, Guid TrackId);

/// <summary>Captures queue order, revision, and its optional playlist backing.</summary>
public sealed record QueueSnapshot(long QueueRevision, ImmutableArray<QueueEntry> Entries, bool AutoSave, PlaylistVersion? BackingPlaylist = null,
    QueueSaveStatus SaveStatus = QueueSaveStatus.NotRequested, PlaylistSavedReceipt? SaveReceipt = null,
    bool Shuffle = false, RepeatMode Repeat = RepeatMode.Off);

/// <summary>Contains the state of a specific deck.</summary>
public sealed record DeckState(DeckId DeckId, Guid? TrackId, TimeSpan Position, TimeSpan? Duration,
    TransportStatus Status, bool Ready, double Gain, string? ErrorCode = null);

/// <summary>Contains master output and fade settings reported by the playback owner.</summary>
public sealed record MasterOutput(double Gain, double CrossfaderPosition, bool AutoFadeEnabled, bool FadeActive,
    ImmutableArray<double> EqualizerBands, bool EqualizerBypassed);

/// <summary>Contains the shared immutable state for all presentations of a playback session.</summary>
/// <remarks>2026-11-24: Carry the selected queue occurrence for FR-PLAYER-001#ac-transport, including repeated tracks.</remarks>
public sealed record PlaybackState(Guid SessionId, long Revision, QueueSnapshot Queue, PlaybackCapabilities Capabilities,
    TransportStatus Status, Guid? ActiveTrackId = null, TimeSpan Position = default, TimeSpan? Duration = null,
    ImmutableArray<DeckState> Decks = default, MasterOutput? Output = null, Guid? ActiveQueueEntryId = null);

/// <summary>Describes one scoped edit of preserve-order queue entries.</summary>
public abstract record QueueEdit
{
    public sealed record Reorder(ImmutableArray<Guid> OrderedEntryIds) : QueueEdit;
    public sealed record Remove(ImmutableArray<Guid> EntryIds) : QueueEdit;
}

/// <summary>Describes one gain or auto-fade adjustment.</summary>
public abstract record MixEdit
{
    public sealed record MasterGain(double Gain) : MixEdit;
    public sealed record DeckGain(DeckId DeckId, double Gain) : MixEdit;
    public sealed record CrossfaderPosition(double Position) : MixEdit;
    public sealed record AutoFadeEnabled(bool Enabled) : MixEdit;
}

/// <summary>Describes an equalizer edit.</summary>
public abstract record EqualizerEdit
{
    public sealed record ApplyBands(ImmutableArray<double> Bands) : EqualizerEdit;
    public sealed record ResetBands : EqualizerEdit;
    public sealed record Bypass(bool Enabled) : EqualizerEdit;
}

/// <summary>Describes a change to the queue's playback policy.</summary>
public abstract record QueuePlaybackEdit
{
    public sealed record Shuffle(bool Enabled) : QueuePlaybackEdit;
    public sealed record Repeat(RepeatMode Mode) : QueuePlaybackEdit;
}

/// <summary>Reads the active playback session.</summary>
public sealed record GetPlaybackState(Guid SessionId) : IQuery<PlaybackState>;

/// <summary>Adds ordered tracks to a playback session.</summary>
public sealed record EnqueueTracks(Guid SessionId, ImmutableArray<Guid> OrderedTrackIds, EnqueueMode Mode) : ICommand<PlaybackState>;

/// <summary>Controls the active track or the named deck.</summary>
public sealed record Transport(Guid SessionId, TransportAction Action, DeckId? DeckId = null) : ICommand<PlaybackState>;

/// <summary>Seeks only the track captured at the start of the user's gesture.</summary>
public sealed record Seek(Guid SessionId, Guid ActiveTrackId, TimeSpan Position, DeckId? DeckId = null) : ICommand<PlaybackState>;

/// <summary>Prepares the requested track on a deck.</summary>
public sealed record CueDeck(Guid SessionId, DeckId DeckId, Guid TrackId) : ICommand<PlaybackState>;

/// <summary>Applies a mixer adjustment.</summary>
public sealed record SetMix(Guid SessionId, MixEdit Edit) : ICommand<PlaybackState>;

/// <summary>Applies an equalizer adjustment.</summary>
public sealed record SetEqualizer(Guid SessionId, EqualizerEdit Edit) : ICommand<PlaybackState>;

/// <summary>Applies a shuffle or repeat adjustment.</summary>
public sealed record SetQueuePlaybackOptions(Guid SessionId, QueuePlaybackEdit Edit) : ICommand<PlaybackState>;

/// <summary>Loads a playlist into the session at the specified committed version.</summary>
public sealed record LoadPlaylistQueue(Guid SessionId, Guid PlaylistId, long ExpectedPlaylistRevision) : ICommand<PlaybackState>;

/// <summary>Edits queue entries at the expected queue and backing playlist revisions.</summary>
public sealed record EditQueue(Guid SessionId, QueueEdit Edit, long ExpectedQueueRevision, long? ExpectedPlaylistRevision) : ICommand<PlaybackState>;

/// <summary>Changes auto-save for the current saved-playlist-backed queue.</summary>
public sealed record SetQueueAutoSave(Guid SessionId, bool Enabled, long ExpectedQueueRevision) : ICommand<PlaybackState>;

/// <summary>Activates a previously proposed playback strategy.</summary>
public sealed record ChangePlaybackStrategy(Guid SessionId, Guid ProposalId, long ExpectedRevision) : ICommand<PlaybackState>;
