using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Resolves the requested catalog track and sends transport to the SoundFlow decks.</summary>
/// <remarks>2026-11-24: Bind cached media to the native backend for FR-PLAYER-001#ac-transport and FR-LIBRARY-001#ac-offline.</remarks>
public sealed class SoundFlowPlaybackTransport(LiteDbCatalogReader catalog, SoundFlowDecks decks) : IPlaybackTransportTarget
{
    /// <inheritdoc />
    public async Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status,
        CancellationToken cancellationToken)
    {
        if (startPosition is not { } position)
            return await decks.ChangeStatusAsync(deck, trackId, status, cancellationToken).ConfigureAwait(false);

        var source = await catalog.ResolveTrackAsync(trackId, cancellationToken).ConfigureAwait(false);
        return source switch
        {
            Outcome<(TrackAvailability, string?)>.Success { Value: (TrackAvailability.Downloaded, { } path) } =>
                await decks.PrepareAsync(deck, trackId, path, position, status, cancellationToken).ConfigureAwait(false),
            Outcome<(TrackAvailability, string?)>.Success => new Outcome<DeckState>.Invalid([new("track-not-available")]),
            Outcome<(TrackAvailability, string?)>.Cancelled => new Outcome<DeckState>.Cancelled(),
            Outcome<(TrackAvailability, string?)>.Conflict conflict => new Outcome<DeckState>.Conflict(conflict.Code, conflict.ActualRevision),
            Outcome<(TrackAvailability, string?)>.Invalid invalid => new Outcome<DeckState>.Invalid(invalid.Issues),
            Outcome<(TrackAvailability, string?)>.DependencyFailure failure => new Outcome<DeckState>.DependencyFailure(failure.Code, failure.Retryable),
            _ => throw new InvalidOperationException("Unexpected media resolution result.")
        };
    }

    /// <inheritdoc />
    public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken cancellationToken) =>
        decks.SeekAsync(deck, trackId, position, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken cancellationToken) =>
        decks.ReadAsync(deck, trackId, cancellationToken);
}
