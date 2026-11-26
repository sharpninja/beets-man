using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Reviews and changes the selected backend on the playback command lane.</summary>
/// <remarks>2026-11-25: Pin CLI profile and playback selection, stop prior decks, and await Play for FR-CLI-001#ac-switch.</remarks>
public sealed partial class PlaybackSessionHandlers : IQueryHandler<GetPlaybackStrategyProposal, PlaybackStrategyProposal>,
    ICommandHandler<ChangePlaybackStrategy, PlaybackState>
{
    private ICliPlaybackTarget? externalBackend;
    private long? activeProfileRevision;
    private MasterOutput? nativeOutput;
    private SwitchCandidate? pendingSwitch;
    private (Guid OperationId, ChangePlaybackStrategy Intent)? completedSwitch;

    /// <inheritdoc />
    public async Task<Outcome<PlaybackStrategyProposal>> HandleAsync(GetPlaybackStrategyProposal query, RequestContext context, CancellationToken token)
    {
        PlaybackStrategyProposal? proposed = null;
        var result = await RunAsync(query.SessionId, async ct =>
        {
            if (query.StrategyId == PlaybackStrategyIds.SoundFlow && query.ProfileId is null)
            {
                proposed = Prepare(query, nativeCapabilities, null);
                return Success();
            }
            if (query.StrategyId != PlaybackStrategyIds.Cli || query.ProfileId is not { } profileId || profileId == Guid.Empty ||
                cliRegistration is null)
                return Invalid("invalid-playback-strategy");
            var loaded = await cliRegistration.Profiles.ReadAsync(profileId, ct).ConfigureAwait(false);
            if (loaded is not Outcome<CliProfileDraft?>.Success found)
                return CopyFailure(loaded);
            if (found.Value is not { } profile)
                return Invalid("cli-profile-not-found");
            var report = CliProfileHandlers.Validate(profile);
            if (!report.Issues.IsEmpty || !report.Capabilities.HasFlag(PlaybackCapabilities.Transport))
                return Invalid("invalid-cli-playback-profile");
            proposed = Prepare(query, report.Capabilities, profile);
            return Success();
        }, token).ConfigureAwait(false);
        return result switch
        {
            Outcome<PlaybackState>.Success => new Outcome<PlaybackStrategyProposal>.Success(proposed!),
            Outcome<PlaybackState>.Invalid invalid => new Outcome<PlaybackStrategyProposal>.Invalid(invalid.Issues),
            Outcome<PlaybackState>.Conflict conflict => new Outcome<PlaybackStrategyProposal>.Conflict(conflict.Code, conflict.ActualRevision),
            Outcome<PlaybackState>.DependencyFailure failure => new Outcome<PlaybackStrategyProposal>.DependencyFailure(failure.Code, failure.Retryable),
            _ => new Outcome<PlaybackStrategyProposal>.Cancelled()
        };
    }

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(ChangePlaybackStrategy command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ct => SwitchAsync(command, context.OperationId, ct), token);

    /// <summary>Stops external audio after the final checkpoint has recorded its last playback state.</summary>
    /// <remarks>2026-11-25: Keep one-shot playback from surviving workspace close for FR-CLI-001#ac-switch while retaining FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
    public Task<Outcome<PlaybackState>> StopExternalPlaybackAsync(Guid sessionId, CancellationToken token) =>
        RunAsync(sessionId, async ct =>
        {
            if (externalBackend is not { } worker ||
                state.Decks.FirstOrDefault(deck => deck.DeckId == PrimaryDeck) is not { Ready: true, TrackId: { } track } current ||
                current.Status == TransportStatus.Stopped)
                return Success();
            var result = await worker.ChangeAsync(current.DeckId, track, null, TransportStatus.Stopped, ct).ConfigureAwait(false);
            if (result is not Outcome<DeckState>.Success { Value: var receipt })
                return CopyFailure(result);
            return MatchesDeck(receipt, current.DeckId, track) && receipt.Status == TransportStatus.Stopped ? Success() : InvalidDeckReceipt();
        }, token);

    /// <summary>Releases the external worker after stopping workspace dispatch. The desktop owns the built-in output.</summary>
    public void CloseExternalPlayback()
    {
        gate.Wait();
        try
        {
            externalBackend?.Dispose();
            externalBackend = null;
        }
        finally { gate.Release(); }
    }

    private PlaybackStrategyProposal Prepare(GetPlaybackStrategyProposal query, PlaybackCapabilities capabilities, CliProfileDraft? profile)
    {
        var changed = query.StrategyId != state.StrategyId || query.ProfileId != state.StrategyProfileId ||
            profile?.Revision != activeProfileRevision;
        var proposal = new PlaybackStrategyProposal(state.SessionId, Guid.NewGuid(), state.Revision, query.StrategyId, capabilities, changed,
            query.ProfileId);
        pendingSwitch = new(proposal, profile, CaptureSwitchPosition());
        return proposal;
    }

    private async Task<Outcome<PlaybackState>> SwitchAsync(ChangePlaybackStrategy command, Guid operation, CancellationToken token)
    {
        if (operation == Guid.Empty)
            return Invalid("invalid-operation-id");
        if (completedSwitch is { } completed && completed.OperationId == operation)
            return command == completed.Intent ? Success() : Conflict("operation-intent-mismatch", state.Revision);
        if (pendingSwitch is not { } candidate || candidate.Proposal.ProposalId != command.ProposalId ||
            candidate.Proposal.Revision != command.ExpectedRevision || candidate.Position != CaptureSwitchPosition())
            return Conflict("stale-playback-strategy-proposal", state.Revision);
        if (!candidate.Proposal.RequiresConfirmation)
            return Success();

        ICliPlaybackTarget? destination = null;
        if (candidate.Profile is { } profile)
        {
            var lookup = await cliRegistration!.Profiles.ReadAsync(profile.ProfileId, token).ConfigureAwait(false);
            if (lookup is not Outcome<CliProfileDraft?>.Success current)
                return CopyFailure(lookup);
            if (current.Value?.Revision != profile.Revision)
                return Conflict("stale-cli-profile", current.Value?.Revision);
            destination = cliRegistration.Create(profile);
        }

        try
        {
            var stopped = await StopBeforeSwitchAsync(token).ConfigureAwait(false);
            if (stopped is not Outcome<PlaybackState>.Success)
                return stopped;
            if (externalBackend is null)
            {
                nativePrimary = PrimaryDeck;
                nativeOutput = state.Output;
            }
            externalBackend?.Dispose();
            externalBackend = destination;
            destination = null;
            PrimaryDeck = externalBackend is null ? nativePrimary : DeckId.A;
            activeProfileRevision = candidate.Profile?.Revision;
            playingOccurrences.Clear();
            pendingSwitch = null;
            completedSwitch = (operation, command);
            var next = state with
            {
                StrategyId = candidate.Proposal.StrategyId, StrategyProfileId = candidate.Proposal.ProfileId,
                Capabilities = candidate.Proposal.Capabilities, Status = TransportStatus.Stopped, Decks = [],
                Position = TimeSpan.Zero, Duration = null, Output = externalBackend is null ? nativeOutput : null
            };
            return await CommitAsync(next, audioApplied: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally { destination?.Dispose(); }
    }

    private async Task<Outcome<PlaybackState>> StopBeforeSwitchAsync(CancellationToken token)
    {
        if (TransportBackend is not { } target)
            return Invalid("playback-transport-unsupported");
        var cancelled = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
        if (cancelled is not Outcome<PlaybackState>.Success)
            return cancelled;
        foreach (var deck in state.Decks.OrderBy(deck => deck.DeckId == PrimaryDeck))
        {
            if (!deck.Ready || deck.TrackId is not { } track || deck.Status == TransportStatus.Stopped)
                continue;
            var stopped = await target.ChangeAsync(deck.DeckId, track, null, TransportStatus.Stopped, token).ConfigureAwait(false);
            if (stopped is not Outcome<DeckState>.Success accepted)
                return CopyFailure(stopped);
            if (!MatchesDeck(accepted.Value, deck.DeckId, track) || accepted.Value.Status != TransportStatus.Stopped)
                return InvalidDeckReceipt();
            var applied = await ApplyDeckResultAsync(stopped, deck.DeckId, track, state.ActiveQueueEntryId, resetFade: false,
                requested: TransportStatus.Stopped).ConfigureAwait(false);
            if (applied is not Outcome<PlaybackState>.Success)
            {
                pendingSwitch = pendingSwitch! with { Position = CaptureSwitchPosition() };
                return applied;
            }
        }
        return Success();
    }

    private SwitchPosition CaptureSwitchPosition() => new(state.Queue.QueueRevision, state.ActiveQueueEntryId, state.ActiveTrackId,
        state.Status, PrimaryDeck, state.StrategyId, state.StrategyProfileId);

    private sealed record SwitchCandidate(PlaybackStrategyProposal Proposal, CliProfileDraft? Profile, SwitchPosition Position);
    private sealed record SwitchPosition(long QueueRevision, Guid? Entry, Guid? Track, TransportStatus Status, DeckId Primary, string Engine, Guid? Profile);
}
