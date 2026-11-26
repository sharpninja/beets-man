using BeetsMan.Desktop.Application.Replication;

namespace BeetsMan.Desktop.Application.Playback;

public sealed partial class PlaybackSessionHandlers
{
    private readonly Dictionary<DeckId, PlaybackPlayOccurrence> playingOccurrences = [];
    private RecordPlay? pendingPlay;

    /// <summary>Records a new occurrence only after the backend confirms that a start reached Playing.</summary>
    /// <remarks>2026-11-25: Keep pause and resume, repeated queue entries, and failed backends distinct for FR-RECORDS-001#ac-offline.</remarks>
    private void ObservePlaybackOccurrence(DeckState applied, Guid track, Guid? entry, TransportStatus requested, bool newOccurrence)
    {
        var current = playingOccurrences.GetValueOrDefault(applied.DeckId);
        if (applied.Status == TransportStatus.Stopped || current is not null && (current.TrackId != track || current.QueueEntryId != entry))
        {
            playingOccurrences.Remove(applied.DeckId);
            current = null;
        }
        if (listeningScope is null || requested != TransportStatus.Playing || applied.Status != TransportStatus.Playing || current is not null && !newOccurrence)
            return;

        var play = new RecordPlay(Guid.NewGuid(), track, listeningClock.GetUtcNow());
        playingOccurrences[applied.DeckId] = new(play.PlayId, track, entry);
        pendingPlay = play;
    }

    /// <summary>Preserves continuity only for the primary occurrence that survives a restart.</summary>
    private PlaybackPlayOccurrence? ActivePlaybackOccurrence(PlaybackState next) =>
        next.ActiveQueueEntryId is Guid entry && playingOccurrences.GetValueOrDefault(PrimaryDeck) is { } occurrence &&
        occurrence.QueueEntryId == entry && occurrence.TrackId == next.ActiveTrackId ? occurrence : null;

    /// <summary>Requires atomic history persistence when a registered listener scope is supplied.</summary>
    private static LibraryRecordScope? ValidateHistoryScope(LibraryRecordScope? scope, IPlaybackCheckpointStore? store)
    {
        if (scope is { } configured && (configured.LibraryId == Guid.Empty || configured.OriginInstanceId == Guid.Empty || store is not IPlaybackHistoryCheckpointStore))
            throw new ArgumentException("Listening history requires a registered library and an atomic playback history store.", nameof(scope));
        return scope;
    }
}
