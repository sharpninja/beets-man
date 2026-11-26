using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Reads the effective duration of an automatic crossfade.</summary>
/// <remarks>2026-11-25: Reuse local and shared playback preferences for FR-PLAYER-001#ac-fade.</remarks>
public interface IPlaybackFadePreference
{
    /// <summary>Resolves the configured duration for the next automatic transition.</summary>
    Task<Outcome<TimeSpan>> ReadFadeDurationAsync(CancellationToken cancellationToken);
}

/// <summary>Advances queue playback and automatic blends from the detected audio position.</summary>
/// <remarks>2026-11-25: Prepare the incoming deck, freeze on pause, and promote its exact queue occurrence for FR-PLAYER-001#ac-fade.</remarks>
public sealed partial class PlaybackSessionHandlers : ICommandHandler<AdvancePlayback, PlaybackState>
{
    private AutomaticFade? automaticFade;
    private Guid? manualFadeEntry;
    private Guid? failedFadeEntry;

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(AdvancePlayback command, CommandContext context, CancellationToken token) =>
        RunAsync(command.SessionId, AdvancePlaybackAsync, token);

    private async Task<Outcome<PlaybackState>> SetAutomaticFadeAsync(bool enabled, CancellationToken token)
    {
        if (automaticFadePreference is null)
            return enabled ? Invalid("playback-automatic-fade-unsupported") : Success();
        if (state.Output?.AutoFadeEnabled == enabled)
            return Success();
        var cancelled = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
        if (cancelled is not Outcome<PlaybackState>.Success)
            return cancelled;
        UpdateFadeOutput(state.Output! with { AutoFadeEnabled = enabled });
        return Success();
    }

    private async Task<Outcome<PlaybackState>> AdvancePlaybackAsync(CancellationToken token)
    {
        if (checkpointPending)
        {
            var written = await PersistAsync(state, token).ConfigureAwait(false);
            if (written is not Outcome<PlaybackState>.Success)
                return written;
        }
        // 2026-11-25: Do not call a CLI worker from the native audio-clock cadence for FR-CLI-001#ac-switch.
        if (externalBackend is not null)
            return Success();
        if ((state.Status != TransportStatus.Playing && (automaticFade is null || state.Status == TransportStatus.Paused)) ||
            TransportBackend is not { } target ||
            state.ActiveTrackId is not Guid track || !state.Decks.Any(deck => deck.DeckId == PrimaryDeck && deck.Ready && deck.TrackId == track))
            return Success();

        var observed = await target.ReadAsync(PrimaryDeck, track, token).ConfigureAwait(false);
        if (observed is not Outcome<DeckState>.Success { Value: var outgoing })
            return CopyFailure(observed);
        if (!MatchesDeck(outgoing, PrimaryDeck, track))
            return InvalidDeckReceipt();
        ObservePrimaryClock(outgoing);
        var entry = NextForAutomaticPlayback();
        if (automaticFade is { } active)
        {
            if (outgoing.Status == TransportStatus.Paused)
                return Success();
            if (entry != active.Entry)
                return await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
            return await AdvanceFadeAsync(active, outgoing, target, token).ConfigureAwait(false);
        }
        var mayAdvance = entry is not null && manualFadeEntry != state.ActiveQueueEntryId && failedFadeEntry != state.ActiveQueueEntryId;
        if (outgoing.Status == TransportStatus.Stopped)
            return mayAdvance ? await StartEntryAsync(entry!, TimeSpan.Zero, token, newOccurrence: true).ConfigureAwait(false)
                : await CommitAsync(state, true, CancellationToken.None).ConfigureAwait(false);

        if (!mayAdvance || outgoing is not { Status: TransportStatus.Playing, Duration: { } duration } ||
            state.Output?.AutoFadeEnabled != true || automaticFadePreference is null || MixBackend is null ||
            mix.CrossfaderPosition != BlendEndpoint(PrimaryDeck))
            return Success();

        var otherDeck = PrimaryDeck == DeckId.A ? DeckId.B : DeckId.A;
        if (state.Decks.Any(deck => deck.DeckId == otherDeck && deck.Status != TransportStatus.Stopped))
            return Success();
        var preference = await automaticFadePreference.ReadFadeDurationAsync(token).ConfigureAwait(false);
        if (preference is not Outcome<TimeSpan>.Success { Value: var length })
            return CopyFailure(preference);
        if (length <= TimeSpan.Zero || duration - outgoing.Position > length)
            return Success();

        return await BeginFadeAsync(target, outgoing, otherDeck, entry!, token).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> BeginFadeAsync(IPlaybackTransportTarget target, DeckState outgoing, DeckId incomingDeck,
        QueueEntry entry, CancellationToken token)
    {
        var alreadyCued = state.Decks.FirstOrDefault(deck => deck.DeckId == incomingDeck && deck.TrackId == entry.TrackId &&
            deck is { Ready: true, Status: TransportStatus.Stopped } && deck.Position == TimeSpan.Zero);
        var prepared = alreadyCued is not null ? new Outcome<DeckState>.Success(alreadyCued) :
            await target.ChangeAsync(incomingDeck, entry.TrackId, TimeSpan.Zero, TransportStatus.Stopped, token).ConfigureAwait(false);
        if (prepared is not Outcome<DeckState>.Success { Value: var cue } || !MatchesDeck(cue, incomingDeck, entry.TrackId) || cue.Status != TransportStatus.Stopped)
            return RejectIncoming(prepared, entry.TrackId);
        state = WithDeck(state, cue);
        var checkedClock = await target.ReadAsync(outgoing.DeckId, outgoing.TrackId!.Value, token).ConfigureAwait(false);
        if (checkedClock is not Outcome<DeckState>.Success { Value: var live })
            return CopyFailure(checkedClock);
        if (!MatchesDeck(live, outgoing.DeckId, outgoing.TrackId.Value))
            return InvalidDeckReceipt();
        ObservePrimaryClock(live);
        if (live.Status == TransportStatus.Paused)
            return Success();

        var end = live.Duration ?? live.Position;
        if (live.Status == TransportStatus.Playing && cue.Duration is { } incomingLength && end - live.Position > incomingLength)
            return Success();

        var started = await target.ChangeAsync(incomingDeck, entry.TrackId, null, TransportStatus.Playing, token).ConfigureAwait(false);
        if (started is not Outcome<DeckState>.Success { Value: var incoming } || !MatchesDeck(incoming, incomingDeck, entry.TrackId) ||
            incoming.Status != TransportStatus.Playing)
            return RejectIncoming(started, entry.TrackId);
        ObserveEngineStart(entry.TrackId, started);
        automaticFade = new(entry, incomingDeck, live.Position, end - live.Position);
        ObservePlaybackOccurrence(incoming, entry.TrackId, entry.EntryId, TransportStatus.Playing, true);
        var next = WithDeck(state, incoming) with { Output = state.Output! with { FadeActive = true, FadeErrorCode = null } };
        var committed = await CommitAsync(next, true, CancellationToken.None).ConfigureAwait(false);
        if (committed is Outcome<PlaybackState>.Success && (live.Status == TransportStatus.Stopped || live.Position >= end))
            return await AdvanceFadeAsync(automaticFade, live, target, CancellationToken.None).ConfigureAwait(false);
        return committed;
    }

    private async Task<Outcome<PlaybackState>> AdvanceFadeAsync(AutomaticFade fade, DeckState outgoing, IPlaybackTransportTarget target,
        CancellationToken token)
    {
        var observed = await target.ReadAsync(fade.IncomingDeck, fade.Entry.TrackId, token).ConfigureAwait(false);
        if (observed is not Outcome<DeckState>.Success { Value: var incoming } || !MatchesDeck(incoming, fade.IncomingDeck, fade.Entry.TrackId) ||
            incoming.Status != TransportStatus.Playing)
        {
            if (observed is Outcome<DeckState>.Cancelled)
                return new Outcome<PlaybackState>.Cancelled();
            var cancelled = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
            return cancelled is Outcome<PlaybackState>.Success ? RejectIncoming(observed, fade.Entry.TrackId) : cancelled;
        }
        var fraction = outgoing.Status == TransportStatus.Stopped || fade.Duration <= TimeSpan.Zero ? 1 :
            Math.Clamp((outgoing.Position - fade.Start).Ticks / (double)fade.Duration.Ticks, 0, 1);
        var position = fade.IncomingDeck == DeckId.B ? fraction : 1 - fraction;
        var mixed = await ApplyMixAsync(MixBackend!, mix with { CrossfaderPosition = position }, false, token).ConfigureAwait(false);
        if (mixed is not Outcome<PlaybackState>.Success)
            return mixed;
        state = WithDeck(state, incoming);
        if (fraction < 1)
            return Success();

        var stopped = outgoing.Status == TransportStatus.Stopped ? new Outcome<DeckState>.Success(outgoing) :
            await target.ChangeAsync(outgoing.DeckId, outgoing.TrackId!.Value, null, TransportStatus.Stopped, CancellationToken.None).ConfigureAwait(false);
        if (stopped is not Outcome<DeckState>.Success { Value: var ended })
            return CopyFailure(stopped);
        if (!MatchesDeck(ended, outgoing.DeckId, outgoing.TrackId!.Value) || ended.Status != TransportStatus.Stopped)
            return InvalidDeckReceipt();
        playingOccurrences.Remove(outgoing.DeckId);
        PrimaryDeck = fade.IncomingDeck;
        automaticFade = null;
        manualFadeEntry = null;
        failedFadeEntry = null;
        var next = WithDeck(state, ended) with { ActiveTrackId = fade.Entry.TrackId, ActiveQueueEntryId = fade.Entry.EntryId,
            Position = incoming.Position, Duration = incoming.Duration, Status = incoming.Status,
            Output = state.Output! with { FadeActive = false, FadeErrorCode = null } };
        return await CommitAsync(next, true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> CancelAutomaticFadeAsync(CancellationToken token)
    {
        var ownsFade = automaticFade is not null;
        if (automaticFade is { } fade)
        {
            var reset = await ApplyMixAsync(MixBackend!, mix with { CrossfaderPosition = BlendEndpoint(PrimaryDeck) }, false, token).ConfigureAwait(false);
            if (reset is not Outcome<PlaybackState>.Success)
                return reset;
            var stopped = await TransportBackend!.ChangeAsync(fade.IncomingDeck, fade.Entry.TrackId, null, TransportStatus.Stopped,
                CancellationToken.None).ConfigureAwait(false);
            if (stopped is not Outcome<DeckState>.Success { Value: var deck })
                return CopyFailure(stopped);
            if (!MatchesDeck(deck, fade.IncomingDeck, fade.Entry.TrackId) || deck.Status != TransportStatus.Stopped)
                return InvalidDeckReceipt();
            playingOccurrences.Remove(deck.DeckId);
            state = WithDeck(state, deck);
            automaticFade = null;
        }
        manualFadeEntry = null;
        failedFadeEntry = null;
        if (state.Output is { } output && (ownsFade || output.FadeErrorCode is not null))
            UpdateFadeOutput(output with { FadeActive = false, FadeErrorCode = null });
        return Success();
    }

    private async Task<Outcome<PlaybackState>> ChangeFadeTransportAsync(AutomaticFade fade, TransportStatus status, CancellationToken token)
    {
        if (state.Status == status)
            return Success();
        var transport = TransportBackend!;
        var incoming = await transport.ChangeAsync(fade.IncomingDeck, fade.Entry.TrackId, null, status, token).ConfigureAwait(false);
        if (incoming is not Outcome<DeckState>.Success { Value: var other })
            return CopyFailure(incoming);
        if (!MatchesDeck(other, fade.IncomingDeck, fade.Entry.TrackId) || other.Status != status)
            return InvalidDeckReceipt();
        var track = state.ActiveTrackId!.Value;
        var outgoing = await transport.ChangeAsync(PrimaryDeck, track, null, status, CancellationToken.None).ConfigureAwait(false);
        if (outgoing is not Outcome<DeckState>.Success { Value: var primary } || !MatchesDeck(primary, PrimaryDeck, track) || primary.Status != status)
        {
            var rolledBack = await transport.ChangeAsync(fade.IncomingDeck, fade.Entry.TrackId, null, state.Status, CancellationToken.None).ConfigureAwait(false);
            if (rolledBack is not Outcome<DeckState>.Success { Value: var verified } || verified.Status != state.Status)
            {
                UpdateFadeOutput(state.Output! with { FadeErrorCode = "playback-fade-transport-incomplete" });
                return new Outcome<PlaybackState>.DependencyFailure("playback-fade-transport-incomplete", true);
            }
            return outgoing is Outcome<DeckState>.Success ? InvalidDeckReceipt() : CopyFailure(outgoing);
        }
        state = WithDeck(state, other);
        return await ApplyDeckResultAsync(outgoing, PrimaryDeck, track, state.ActiveQueueEntryId, false, status).ConfigureAwait(false);
    }

    /// <summary>Retains the outgoing deck and records the failed incoming track for notification.</summary>
    /// <remarks>2026-11-25: Tag the incoming failure for FR-PLAYER-001#ac-fade and FR-SETTINGS-001#ac-precedence.</remarks>
    private Outcome<PlaybackState> RejectIncoming(Outcome<DeckState> rejected, Guid track)
    {
        if (rejected is Outcome<DeckState>.Cancelled)
            return new Outcome<PlaybackState>.Cancelled();
        failedFadeEntry = state.ActiveQueueEntryId;
        var error = rejected is Outcome<DeckState>.Invalid invalid && invalid.Issues.Any(issue => issue.Code == "track-not-available")
            ? "playback-fade-incoming-unavailable" : "playback-fade-incoming-failed";
        ReportEngineFailure(track, error);
        UpdateFadeOutput(state.Output! with { FadeActive = false, FadeErrorCode = error });
        return Success();
    }

    private QueueEntry? NextForAutomaticPlayback() => state.ActiveQueueEntryId is not { } entry ? null :
        state.Queue.Repeat == RepeatMode.One ? state.Queue.Entries.FirstOrDefault(item => item.EntryId == entry) : AdjacentEntry(1);

    private void ObservePrimaryClock(DeckState deck)
    {
        var next = WithDeck(state, deck) with { Position = deck.Position, Duration = deck.Duration, Status = deck.Status };
        if (next != state)
            state = next with { Revision = checked(state.Revision + 1) };
    }

    private static PlaybackState WithDeck(PlaybackState value, DeckState deck)
    {
        var earlier = value.Decks.FirstOrDefault(item => item.DeckId == deck.DeckId);
        return earlier == deck ? value : value with { Decks = earlier is null ? value.Decks.Add(deck) : value.Decks.Replace(earlier, deck) };
    }

    private void UpdateFadeOutput(MasterOutput next)
    {
        if (state.Output != next)
            state = state with { Output = next, Revision = checked(state.Revision + 1) };
    }

    private static int BlendEndpoint(DeckId deck) => deck == DeckId.A ? 0 : 1;
    private Outcome<PlaybackState> Success() => new Outcome<PlaybackState>.Success(state);
    private static Outcome<PlaybackState> InvalidDeckReceipt() => new Outcome<PlaybackState>.DependencyFailure("invalid-playback-deck-receipt", false);

    private sealed record AutomaticFade(QueueEntry Entry, DeckId IncomingDeck, TimeSpan Start, TimeSpan Duration);
}
