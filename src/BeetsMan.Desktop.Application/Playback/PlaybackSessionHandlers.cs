using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Seeks the captured track on one backend deck and returns the applied state.</summary>
/// <remarks>2026-11-24: Define the backend seek and fade reset boundary for FR-PLAYER-001#ac-transport and ac-fade.</remarks>
public interface IPlaybackSeekTarget
{
    /// <summary>Abandons a pending automatic fade and seeks only if the named track is still loaded.</summary>
    Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken cancellationToken);
}

/// <summary>Applies a transport change to one deck through the selected backend.</summary>
/// <remarks>2026-11-24: Define the deck replacement boundary for FR-PLAYER-001#ac-transport and ac-fade.</remarks>
public interface IPlaybackTransportTarget : IPlaybackSeekTarget
{
    /// <summary>Prepares a replacement when a start position is supplied, or changes the status of the captured loaded track.</summary>
    Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken cancellationToken);

    /// <summary>Reads the current audio clock for the captured deck and track.</summary>
    Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken cancellationToken);
}

/// <summary>Owns the session, queue navigation, and coordinated transport changes.</summary>
/// <remarks>2026-11-24: Persist and operate the queue and transport for FR-PLAYER-001#ac-transport and ac-mix.</remarks>
/// <remarks>2026-11-25: Capture successful deck starts with their checkpoint for FR-RECORDS-001#ac-offline.</remarks>
/// <remarks>2026-11-25: Report failed engine starts independently of the calling command for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed partial class PlaybackSessionHandlers(PlaybackState initialState, DeckId primaryDeck, IPlaybackSeekTarget backend,
    IPlaybackCheckpointStore? checkpoints = null, QueuePlaylistPublication? queuePlaylists = null,
    LibraryRecordScope? historyScope = null, TimeProvider? clock = null, IPlaybackRestartPreference? restartPreference = null,
    IPlaybackFadePreference? fadePreference = null, CliPlaybackRegistration? cli = null) :
    IQueryHandler<GetPlaybackState, PlaybackState>, ICommandHandler<Seek, PlaybackState>, ICommandHandler<Transport, PlaybackState>,
    ICommandHandler<EnqueueTracks, PlaybackState>, ICommandHandler<CueDeck, PlaybackState>,
    ICommandHandler<SetQueuePlaybackOptions, PlaybackState>
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private PlaybackState state = initialState with
    {
        Decks = initialState.Decks.IsDefault ? [] : initialState.Decks,
        Queue = initialState.Queue.Entries.IsDefault ? initialState.Queue with { Entries = [] } : initialState.Queue,
        Output = InitialOutput(initialState, primaryDeck)
    };
    private PlaybackMixSettings mix = InitialMix(initialState, primaryDeck);
    private bool restored;
    private readonly IPlaybackCheckpointStore? checkpointStore = queuePlaylists?.Store ?? checkpoints;
    private readonly QueuePlaylistPublication? playlistPublication = queuePlaylists;
    private readonly bool initialAutoSave = initialState.Queue.AutoSave;
    private readonly LibraryRecordScope? listeningScope = ValidateHistoryScope(historyScope, queuePlaylists?.Store ?? checkpoints);
    private readonly TimeProvider listeningClock = clock ?? TimeProvider.System;
    private DeckId PrimaryDeck { get; set; } = primaryDeck;
    private readonly IPlaybackSeekTarget nativeBackend = backend;
    private readonly PlaybackCapabilities nativeCapabilities = initialState.Capabilities;
    private readonly CliPlaybackRegistration? cliRegistration = cli;
    private DeckId nativePrimary = primaryDeck;
    private IPlaybackSeekTarget Backend => (IPlaybackSeekTarget?)externalBackend ?? nativeBackend;
    private IPlaybackTransportTarget? TransportBackend => Backend as IPlaybackTransportTarget;
    private IPlaybackMixTarget? MixBackend => Backend as IPlaybackMixTarget;
    private readonly IPlaybackRestartPreference? startupPreference = restartPreference;
    private readonly IPlaybackFadePreference? automaticFadePreference = fadePreference;
    private long checkpointRevision;
    private bool checkpointPending;
    /// <summary>Remembers operations that changed audio but failed to save their checkpoint in this session.</summary>
    /// <remarks>2026-11-25: Preserve retry identities across intervening commands for FR-PLAYER-001#ac-transport and TEST-PLAYER-001.</remarks>
    private readonly Dictionary<Guid, object> checkpointRetryIntents = [];
    private long visualizationDiscontinuity;
    private PlaybackEngineFailure? lastEngineFailure;

    /// <summary>Reports an engine failure to a nonblocking workspace subscriber.</summary>
    public event Action<PlaybackEngineFailure>? EngineFailed;

    /// <summary>Captures deck and strategy status without reading a device or occupying the playback command lane.</summary>
    /// <remarks>2026-11-25: Fence changes in rendered audio for FR-PLUGINS-001#ac-visualize.</remarks>
    public VisualizationPlaybackContext ReadVisualizationContext()
    {
        var before = Volatile.Read(ref visualizationDiscontinuity);
        var snapshot = Volatile.Read(ref state);
        var after = Volatile.Read(ref visualizationDiscontinuity);
        return new(snapshot.Capabilities.HasFlag(PlaybackCapabilities.Visualization), snapshot.ActiveQueueEntryId,
            PlayingTrack(DeckId.A), PlayingTrack(DeckId.B), before == after ? after : after | 1);
        Guid? PlayingTrack(DeckId id) => snapshot.Decks.FirstOrDefault(deck => deck.DeckId == id && deck.Status == TransportStatus.Playing)?.TrackId;
    }

    /// <summary>Registers the session query and supported transport requests.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) =>
        builder.RegisterQuery<GetPlaybackState, PlaybackState>(this).RegisterCommand<Seek, PlaybackState>(this)
            .RegisterCommand<Transport, PlaybackState>(this).RegisterCommand<EnqueueTracks, PlaybackState>(this)
            .RegisterCommand<CueDeck, PlaybackState>(this).RegisterCommand<SetQueuePlaybackOptions, PlaybackState>(this)
            .RegisterCommand<LoadPlaylistQueue, PlaybackState>(this).RegisterCommand<EditQueue, PlaybackState>(this)
            .RegisterCommand<SetQueueAutoSave, PlaybackState>(this)
            .RegisterCommand<InitializePlayback, PlaybackState>(this).RegisterCommand<SavePlaybackForRestart, PlaybackState>(this)
            .RegisterCommand<SetMix, PlaybackState>(this).RegisterCommand<SetEqualizer, PlaybackState>(this)
        .RegisterCommand<AdvancePlayback, PlaybackState>(this)
        .RegisterQuery<GetPlaybackStrategyProposal, PlaybackStrategyProposal>(this)
        .RegisterCommand<ChangePlaybackStrategy, PlaybackState>(this);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(GetPlaybackState query, RequestContext context, CancellationToken token) =>
        RunAsync(query.SessionId, ReadLiveStateAsync, token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(Seek command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, cancellationToken => ApplySeekAsync(command, cancellationToken), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(Transport command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, cancellationToken => ApplyTransportAsync(command, cancellationToken), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(EnqueueTracks command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, cancellationToken => EnqueueAsync(command, context, cancellationToken), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(CueDeck command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, cancellationToken => CueAsync(command, cancellationToken), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(SetQueuePlaybackOptions command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, cancellationToken => ChangeOptionsAsync(command.Edit, cancellationToken), token);

    private async Task<Outcome<PlaybackState>> ApplySeekAsync(Seek command, CancellationToken token)
    {
        if (!state.Capabilities.HasFlag(PlaybackCapabilities.Seek))
            return new Outcome<PlaybackState>.Invalid([new("playback-seek-unsupported")]);
        var deckId = command.DeckId ?? PrimaryDeck;
        if (!Enum.IsDefined(deckId) || command.ActiveTrackId == Guid.Empty || command.Position < TimeSpan.Zero)
            return new Outcome<PlaybackState>.Invalid([new("invalid-playback-seek")]);

        var deck = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        var track = command.DeckId.HasValue ? deck?.TrackId : state.ActiveTrackId;
        if (track != command.ActiveTrackId || deck is not null && (deck.TrackId != track || !deck.Ready))
            return new Outcome<PlaybackState>.Conflict("seek-track-changed", state.Revision);
        var duration = deck?.Duration ?? (deckId == PrimaryDeck ? state.Duration : null);
        if (duration is { } end && command.Position > end)
            return new Outcome<PlaybackState>.Invalid([new("seek-position-out-of-range", nameof(command.Position))]);

        var reset = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
        if (reset is not Outcome<PlaybackState>.Success)
            return reset;
        var outcome = await Backend.SeekAsync(deckId, command.ActiveTrackId, command.Position, token).ConfigureAwait(false);
        return await ApplyDeckResultAsync(outcome, deckId, command.ActiveTrackId, state.ActiveQueueEntryId, true).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> EnqueueAsync(EnqueueTracks command, CommandContext context, CancellationToken token)
    {
        var operationId = context.OperationId;
        if (operationId == Guid.Empty || !Enum.IsDefined(command.Mode) || command.OrderedTrackIds.IsDefaultOrEmpty ||
            command.OrderedTrackIds.Any(id => id == Guid.Empty))
            return Invalid("invalid-enqueue-request");
        if (command.Mode == EnqueueMode.PlayNow && !CanTransport())
            return Invalid("playback-transport-unsupported");

        var intent = HashIntent(new { Kind = nameof(EnqueueTracks), command.Mode, command.OrderedTrackIds });
        var added = command.OrderedTrackIds.Select((track, index) => new QueueEntry(QueueEntryId(operationId, index), track)).ToImmutableArray();
        var replay = await ReplayKnownQueueSaveAsync(context, intent, token).ConfigureAwait(false);
        if (replay is not null && replay is not Outcome<PlaybackState>.Success)
            return replay;
        if (replay is not null && !state.Queue.Entries.Contains(added[0]))
            return replay;
        var byEntry = state.Queue.Entries.ToDictionary(entry => entry.EntryId);
        var existing = added.Count(entry => byEntry.ContainsKey(entry.EntryId));
        if (existing != 0 && (existing != added.Length || added.Any(entry => byEntry[entry.EntryId] != entry)))
            return new Outcome<PlaybackState>.Conflict("enqueue-operation-reused", state.Queue.QueueRevision);
        if (existing == 0)
        {
            var queue = state.Queue with { Entries = state.Queue.Entries.AddRange(added), QueueRevision = checked(state.Queue.QueueRevision + 1) };
            var committed = await CommitQueueEditAsync(state with { Queue = queue }, context, intent, token).ConfigureAwait(false);
            if (committed is not Outcome<PlaybackState>.Success || command.Mode == EnqueueMode.Append || token.IsCancellationRequested)
                return committed;
        }
        if (command.Mode == EnqueueMode.Append || state.ActiveQueueEntryId == added[0].EntryId && state.Status == TransportStatus.Playing)
            return new Outcome<PlaybackState>.Success(state);
        return await StartEntryAsync(added[0], TimeSpan.Zero, token).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> CueAsync(CueDeck command, CancellationToken token)
    {
        if (!CanTransport() || !state.Capabilities.HasFlag(PlaybackCapabilities.Mixer))
            return Invalid("playback-cue-unsupported");
        if (!Enum.IsDefined(command.DeckId) || command.TrackId == Guid.Empty)
            return Invalid("invalid-playback-cue");
        var reset = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
        return reset is Outcome<PlaybackState>.Success
            ? await ChangeAsync(command.DeckId, command.TrackId, TimeSpan.Zero, TransportStatus.Stopped,
                state.ActiveTrackId == command.TrackId ? state.ActiveQueueEntryId : null, false, token).ConfigureAwait(false) : reset;
    }

    private Task<Outcome<PlaybackState>> ChangeOptionsAsync(QueuePlaybackEdit edit, CancellationToken token)
    {
        QueueSnapshot? queue = edit switch
        {
            QueuePlaybackEdit.Shuffle shuffle => state.Queue with { Shuffle = shuffle.Enabled },
            QueuePlaybackEdit.Repeat repeat when Enum.IsDefined(repeat.Mode) => state.Queue with { Repeat = repeat.Mode },
            _ => null
        };
        if (queue is null)
            return Task.FromResult(Invalid("invalid-queue-playback-options"));
        return queue == state.Queue
            ? Task.FromResult<Outcome<PlaybackState>>(new Outcome<PlaybackState>.Success(state))
            : CommitAsync(state with { Queue = queue }, false, token);
    }

    private async Task<Outcome<PlaybackState>> ApplyTransportAsync(Transport command, CancellationToken token)
    {
        if (!CanTransport())
            return Invalid("playback-transport-unsupported");
        var deckId = command.DeckId ?? PrimaryDeck;
        if (!Enum.IsDefined(command.Action) || !Enum.IsDefined(deckId))
            return Invalid("invalid-transport-request");
        if (command.Action is TransportAction.Next or TransportAction.Previous)
        {
            if (command.DeckId.HasValue)
                return Invalid("queue-navigation-requires-primary-transport");
            var entry = AdjacentEntry(command.Action == TransportAction.Next ? 1 : -1);
            return entry is null
                ? new Outcome<PlaybackState>.Success(state)
                : await StartEntryAsync(entry, TimeSpan.Zero, token, newOccurrence: true).ConfigureAwait(false);
        }

        var currentDeck = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        var status = command.Action switch
        {
            TransportAction.Play => TransportStatus.Playing,
            TransportAction.Pause => TransportStatus.Paused,
            TransportAction.TogglePlayPause => (currentDeck?.Status ?? state.Status) == TransportStatus.Playing
                ? TransportStatus.Paused : TransportStatus.Playing,
            _ => TransportStatus.Stopped
        };
        if (automaticFade is { } fade && !command.DeckId.HasValue && status is TransportStatus.Playing or TransportStatus.Paused)
            return await ChangeFadeTransportAsync(fade, status, token).ConfigureAwait(false);
        if (status == TransportStatus.Stopped || command.DeckId.HasValue && automaticFade is not null)
        {
            var reset = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
            if (reset is not Outcome<PlaybackState>.Success)
                return reset;
        }
        var deck = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        if (!command.DeckId.HasValue && status == TransportStatus.Playing && state.ActiveQueueEntryId is null && playlistBinding is not null &&
            AdjacentEntry(1) is { } fromPlaylist)
            return await StartEntryAsync(fromPlaylist, TimeSpan.Zero, token).ConfigureAwait(false);
        if (deck is { Ready: true, TrackId: { } track })
            return await ChangeAsync(deckId, track, null, status, state.ActiveQueueEntryId, status == TransportStatus.Stopped, token).ConfigureAwait(false);

        if (command.DeckId.HasValue)
            return Invalid("deck-not-prepared");
        if (status != TransportStatus.Playing)
            return new Outcome<PlaybackState>.Success(state);
        var selected = state.Queue.Entries.FirstOrDefault(entry => entry.EntryId == state.ActiveQueueEntryId);
        var first = selected ?? AdjacentEntry(1);
        return first is null ? Invalid("playback-queue-empty") :
            await StartEntryAsync(first, selected is null ? TimeSpan.Zero : state.Position, token).ConfigureAwait(false);
    }

    private QueueEntry? AdjacentEntry(int direction)
    {
        var entries = state.Queue.Entries;
        if (entries.IsEmpty)
            return null;
        if (state.Queue.Shuffle)
            entries = [.. entries.OrderBy(entry => QueueEntryId(entry.EntryId, 0))];
        var current = entries.IndexOf(entries.FirstOrDefault(entry => entry.EntryId == state.ActiveQueueEntryId)!);
        if (current < 0)
            return direction > 0 ? entries[0] : null;
        var target = current + direction;
        if (target >= 0 && target < entries.Length)
            return entries[target];
        return state.Queue.Repeat == RepeatMode.All ? entries[target < 0 ? entries.Length - 1 : 0] : null;
    }

    private async Task<Outcome<PlaybackState>> StartEntryAsync(QueueEntry entry, TimeSpan position, CancellationToken token, bool newOccurrence = false)
    {
        var reset = await CancelAutomaticFadeAsync(token).ConfigureAwait(false);
        return reset is Outcome<PlaybackState>.Success
            ? await ChangeAsync(PrimaryDeck, entry.TrackId, position, TransportStatus.Playing, entry.EntryId, true, token, newOccurrence).ConfigureAwait(false)
            : reset;
    }

    private async Task<Outcome<PlaybackState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status,
        Guid? queueEntry, bool resetFade, CancellationToken token, bool newOccurrence = false)
    {
        var target = (IPlaybackTransportTarget)Backend;
        var result = await target.ChangeAsync(deck, track, position, status, token).ConfigureAwait(false);
        if (status == TransportStatus.Playing)
            ObserveEngineStart(track, result);
        return await ApplyDeckResultAsync(result, deck, track, queueEntry, resetFade, status, newOccurrence).ConfigureAwait(false);
    }

    /// <summary>Reports a new engine failure and keeps repeated failures of the same start quiet.</summary>
    /// <remarks>2026-11-25: Coalesce continuing engine errors for the STB07 playback flag in FR-SETTINGS-001#ac-precedence.</remarks>
    private void ObserveEngineStart(Guid track, Outcome<DeckState> result)
    {
        if (result is Outcome<DeckState>.DependencyFailure failure)
            ReportEngineFailure(track, failure.Code);
        else if (result is Outcome<DeckState>.Success { Value.Status: TransportStatus.Playing })
            lastEngineFailure = null;
    }

    private void ReportEngineFailure(Guid track, string code)
    {
        var failure = new PlaybackEngineFailure(state.SessionId, track, state.StrategyId, code);
        if (failure == lastEngineFailure)
            return;
        lastEngineFailure = failure;
        EngineFailed?.Invoke(failure);
    }

    private async Task<Outcome<PlaybackState>> ApplyDeckResultAsync(Outcome<DeckState> result, DeckId deckId, Guid track, Guid? queueEntry,
        bool resetFade, TransportStatus? requested = null, bool newOccurrence = false)
    {
        if (result is not Outcome<DeckState>.Success success)
            return CopyFailure(result);
        var applied = success.Value;
        if (!MatchesDeck(applied, deckId, track))
            return new Outcome<PlaybackState>.DependencyFailure("invalid-playback-deck-receipt", false);
        if (requested is { } intent)
            ObservePlaybackOccurrence(applied, track, deckId == PrimaryDeck ? queueEntry : null, intent, newOccurrence);

        var previous = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        var next = state with
        {
            Decks = previous is null ? state.Decks.Add(applied) : state.Decks.Replace(previous, applied),
            Output = resetFade && state.Output is { FadeActive: true } output ? output with { FadeActive = false } : state.Output
        };
        if (deckId != PrimaryDeck)
        {
            if (pendingPlay is not null)
                return await CommitAsync(next, true, CancellationToken.None).ConfigureAwait(false);
            state = next with { Revision = checked(state.Revision + 1) };
            return new Outcome<PlaybackState>.Success(state);
        }
        return await CommitAsync(next with { ActiveTrackId = track, ActiveQueueEntryId = queueEntry,
            Position = applied.Position, Duration = applied.Duration, Status = applied.Status }, true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> ReadLiveStateAsync(CancellationToken token)
    {
        if (playlistBinding?.StagedSave is not null)
            await RefreshSaveStatusAsync(token).ConfigureAwait(false);
        if (Backend is not IPlaybackTransportTarget target)
            return new Outcome<PlaybackState>.Success(state);
        var snapshot = state;
        foreach (var deck in state.Decks)
        {
            if (!deck.Ready || deck.TrackId is not { } track)
                continue;
            var result = await target.ReadAsync(deck.DeckId, track, token).ConfigureAwait(false);
            if (result is not Outcome<DeckState>.Success read)
                return CopyFailure(result);
            if (!MatchesDeck(read.Value, deck.DeckId, track))
                return new Outcome<PlaybackState>.DependencyFailure("invalid-playback-deck-receipt", false);
            if (read.Value == deck)
                continue;
            var current = read.Value;
            snapshot = snapshot with { Decks = snapshot.Decks.Replace(deck, current) };
            if (deck.DeckId == PrimaryDeck)
                snapshot = snapshot with { Position = current.Position, Duration = current.Duration, Status = current.Status };
        }
        return new Outcome<PlaybackState>.Success(snapshot);
    }

    private async Task<Outcome<PlaybackState>> CommitAsync(PlaybackState next, bool audioApplied, CancellationToken token)
    {
        next = next with { Revision = checked(state.Revision + 1) };
        if (audioApplied)
            state = next;
        var write = await PersistAsync(next, token).ConfigureAwait(false);
        if (write is not Outcome<PlaybackState>.Success)
        {
            checkpointPending = audioApplied;
            return write;
        }
        state = next;
        return write;
    }

    private async Task<Outcome<PlaybackState>> PersistAsync(PlaybackState next, CancellationToken token)
    {
        if (checkpointStore is not null)
        {
            var checkpoint = MakeCheckpoint(next, playlistBinding);
            var write = pendingPlay is { } play && listeningScope is { } scope
                ? await ((IPlaybackHistoryCheckpointStore)checkpointStore).SaveCheckpointWithPlayAsync(checkpoint, checkpointRevision, scope, play, token).ConfigureAwait(false)
                : await checkpointStore.SaveCheckpointAsync(checkpoint, checkpointRevision, token).ConfigureAwait(false);
            if (write is not Outcome<PlaybackCheckpoint>.Success saved)
                return CopyFailure(write);
            checkpointRevision = saved.Value.CheckpointRevision;
            pendingPlay = null;
        }
        checkpointPending = false;
        return new Outcome<PlaybackState>.Success(next);
    }

    /// <summary>Restores the locally checkpointed queue, playlist binding, and playback position.</summary>
    /// <remarks>2026-11-25: Restore the name from the durable playlist binding for FR-PLAYER-001#ac-queue-autosave.</remarks>
    private async Task<Outcome<PlaybackState>> RestoreAsync(CancellationToken token)
    {
        if (!restored && checkpointStore is not null)
        {
            var result = await checkpointStore.ReadCheckpointAsync(state.SessionId, token).ConfigureAwait(false);
            if (result is not Outcome<PlaybackCheckpoint?>.Success read)
                return CopyFailure(result);
            if (read.Value is { } saved)
            {
                checkpointRevision = saved.CheckpointRevision;
                restartCheckpoint = saved;
                if (saved.ActivePlay is { } occurrence)
                    playingOccurrences[PrimaryDeck] = occurrence;
                playlistBinding = saved.PlaylistBacking;
                state = state with
                {
                    Queue = state.Queue with { Entries = saved.Queue, QueueRevision = saved.QueueRevision, Shuffle = saved.Shuffle, Repeat = saved.Repeat,
                        BackingPlaylist = playlistBinding?.Version, AutoSave = playlistBinding is not null && initialAutoSave,
                        BackingPlaylistName = playlistBinding?.Name,
                        SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null, BatchSave = null },
                    ActiveQueueEntryId = saved.ActiveEntryId,
                    ActiveTrackId = saved.Queue.FirstOrDefault(entry => entry.EntryId == saved.ActiveEntryId)?.TrackId,
                    Position = saved.Position, Duration = null, Status = TransportStatus.Stopped,
                    Decks = [.. state.Decks.Where(deck => deck.DeckId != PrimaryDeck)]
                };
                await RefreshSaveStatusAsync(token).ConfigureAwait(false);
            }
        }
        restored = true;
        return new Outcome<PlaybackState>.Success(state);
    }

    /// <summary>Serializes playback changes and repairs applied effects before running new transport operations.</summary>
    /// <remarks>2026-11-25: Replaying an applied effect returns the session's current state for FR-PLAYER-001#ac-transport.</remarks>
    private Task<Outcome<PlaybackState>> RunCommandAsync(Guid sessionId, object intent, CommandContext context,
        Func<CancellationToken, Task<Outcome<PlaybackState>>> operation, CancellationToken token) => RunAsync(sessionId, async ct =>
    {
        if (intent is not InitializePlayback)
            startupSettled = true;
        if (playlistBinding?.StagedSave is not null && intent is EnqueueTracks or EditQueue or LoadPlaylistQueue)
            await RefreshSaveStatusAsync(ct).ConfigureAwait(false);
        var alreadyApplied = checkpointRetryIntents.TryGetValue(context.OperationId, out var priorIntent);
        if (alreadyApplied && !Equals(priorIntent, intent))
            return new Outcome<PlaybackState>.Conflict("playback-operation-reused", state.Revision);
        if (checkpointPending)
        {
            var persisted = await PersistAsync(state, ct).ConfigureAwait(false);
            if (persisted is not Outcome<PlaybackState>.Success)
                return persisted;
        }
        if (alreadyApplied)
            return new Outcome<PlaybackState>.Success(state);
        var before = state.Revision;
        var changesAudioContext = intent is Seek or Transport or CueDeck or InitializePlayback or ChangePlaybackStrategy or EnqueueTracks { Mode: EnqueueMode.PlayNow };
        if (changesAudioContext)
            Interlocked.Increment(ref visualizationDiscontinuity);
        Outcome<PlaybackState> result;
        try { result = await operation(ct).ConfigureAwait(false); }
        finally
        {
            if (changesAudioContext)
                Interlocked.Increment(ref visualizationDiscontinuity);
        }
        if (state.Revision != before && checkpointPending)
            checkpointRetryIntents.Add(context.OperationId, intent);
        return result;
    }, token);

    private bool CanTransport() => TransportBackend is not null && state.Capabilities.HasFlag(PlaybackCapabilities.Transport);

    private static bool MatchesDeck(DeckState deck, DeckId deckId, Guid trackId) =>
        deck.DeckId == deckId && deck.TrackId == trackId && deck.Ready && deck.Position >= TimeSpan.Zero &&
        (deck.Duration is not { } end || deck.Position <= end);

    private static Guid QueueEntryId(Guid key, int index)
    {
        Span<byte> input = stackalloc byte[20];
        key.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], index);
        Span<byte> result = stackalloc byte[32];
        SHA256.HashData(input, result);
        return new Guid(result[..16]);
    }

    private async Task<Outcome<PlaybackState>> RunAsync(Guid sessionId,
        Func<CancellationToken, Task<Outcome<PlaybackState>>> operation, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (sessionId != state.SessionId)
                    return Invalid("playback-session-not-found");
                var ready = await RestoreAsync(token).ConfigureAwait(false);
                return ready is Outcome<PlaybackState>.Success ? await operation(token).ConfigureAwait(false) : ready;
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<PlaybackState>.Cancelled();
        }
    }

    private static Outcome<PlaybackState> Invalid(string code) => new Outcome<PlaybackState>.Invalid([new(code)]);

    private Outcome<PlaybackState> CopyFailure<T>(Outcome<T> failure) => failure switch
    {
        Outcome<T>.Conflict conflict => new Outcome<PlaybackState>.Conflict(conflict.Code, conflict.ActualRevision ?? state.Revision),
        Outcome<T>.Invalid invalid => new Outcome<PlaybackState>.Invalid(invalid.Issues),
        Outcome<T>.Cancelled => new Outcome<PlaybackState>.Cancelled(),
        Outcome<T>.DependencyFailure unavailable => new Outcome<PlaybackState>.DependencyFailure(unavailable.Code, unavailable.Retryable),
        _ => throw new InvalidOperationException("Unexpected playback outcome.")
    };
}
