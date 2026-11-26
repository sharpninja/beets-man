using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Owns explicit startup and shutdown handoffs for the current playback session.</summary>
/// <remarks>2026-11-25: Gate playlist restart on local opt-in for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public sealed partial class PlaybackSessionHandlers : ICommandHandler<InitializePlayback, PlaybackState>,
    ICommandHandler<SavePlaybackForRestart, PlaybackState>
{
    private PlaybackCheckpoint? restartCheckpoint;
    private bool startupSettled;

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(InitializePlayback command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ResumeAtStartupAsync, token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(SavePlaybackForRestart command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, SavePositionForRestartAsync, token);

    /// <summary>Starts only the previously playing entry belonging to a saved playlist.</summary>
    private async Task<Outcome<PlaybackState>> ResumeAtStartupAsync(CancellationToken token)
    {
        if (startupSettled || startupPreference is null ||
            restartCheckpoint is not { WasPlaying: true, ActiveEntryId: Guid active, PlaylistBacking.Version: { PlaylistId: var playlist, Revision: > 0 } } saved ||
            playlist == Guid.Empty || state.ActiveQueueEntryId != active)
            return FinishStartup();
        var enabled = await startupPreference.ReadRestartPreferenceAsync(token).ConfigureAwait(false);
        if (enabled is not Outcome<bool>.Success preference)
            return CopyFailure(enabled);
        if (!preference.Value)
            return FinishStartup();
        if (!CanTransport())
            return Invalid("playback-transport-unsupported");
        var entry = state.Queue.Entries.FirstOrDefault(item => item.EntryId == active);
        if (entry is null || entry.TrackId != state.ActiveTrackId)
            return FinishStartup();
        var result = await StartEntryAsync(entry, saved.Position, token).ConfigureAwait(false);
        if (result is Outcome<PlaybackState>.Success || state.Status == TransportStatus.Playing)
            startupSettled = true;
        return result is Outcome<PlaybackState>.Invalid unavailable && unavailable.Issues.Any(issue => issue.Code == "track-not-available")
            ? new Outcome<PlaybackState>.DependencyFailure("playback-resume-track-unavailable", true)
            : result;

        Outcome<PlaybackState> FinishStartup()
        {
            startupSettled = true;
            restartCheckpoint = null;
            return new Outcome<PlaybackState>.Success(state);
        }
    }

    /// <summary>Persists the final observed primary position without changing deck transport.</summary>
    private async Task<Outcome<PlaybackState>> SavePositionForRestartAsync(CancellationToken token)
    {
        var primary = state.Decks.FirstOrDefault(deck => deck.DeckId == PrimaryDeck);
        if (state.ActiveQueueEntryId is null)
            return new Outcome<PlaybackState>.Success(state);
        if (TransportBackend is not { } target ||
            primary is not { Ready: true, TrackId: Guid track } || track != state.ActiveTrackId)
            return await CommitAsync(state, false, token).ConfigureAwait(false);
        var observed = await target.ReadAsync(PrimaryDeck, track, token).ConfigureAwait(false);
        if (observed is not Outcome<DeckState>.Success live)
            return CopyFailure(observed);
        if (!MatchesDeck(live.Value, PrimaryDeck, track))
            return new Outcome<PlaybackState>.DependencyFailure("invalid-playback-deck-receipt", false);
        var deck = live.Value;
        return await CommitAsync(state with { Decks = state.Decks.Replace(primary, deck), Position = deck.Position,
            Duration = deck.Duration, Status = deck.Status }, false, token).ConfigureAwait(false);
    }
}
