using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Desktop.Application.Dispatch;

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
public sealed partial class PlaybackSessionHandlers(PlaybackState initialState, DeckId primaryDeck, IPlaybackSeekTarget backend,
    IPlaybackCheckpointStore? checkpoints = null, QueuePlaylistPublication? queuePlaylists = null) :
    IQueryHandler<GetPlaybackState, PlaybackState>, ICommandHandler<Seek, PlaybackState>, ICommandHandler<Transport, PlaybackState>,
    ICommandHandler<EnqueueTracks, PlaybackState>, ICommandHandler<CueDeck, PlaybackState>,
    ICommandHandler<SetQueuePlaybackOptions, PlaybackState>
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private PlaybackState state = initialState with
    {
        Decks = initialState.Decks.IsDefault ? [] : initialState.Decks,
        Queue = initialState.Queue.Entries.IsDefault ? initialState.Queue with { Entries = [] } : initialState.Queue
    };
    private bool restored;
    private readonly IPlaybackCheckpointStore? checkpointStore = queuePlaylists?.Store ?? checkpoints;
    private readonly QueuePlaylistPublication? playlistPublication = queuePlaylists;
    private readonly bool initialAutoSave = initialState.Queue.AutoSave;
    private long checkpointRevision;
    private bool checkpointPending;
    private (Guid OperationId, object Intent, long Revision)? pendingEffect;

    /// <summary>Registers the session query and supported transport requests.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) =>
        builder.RegisterQuery<GetPlaybackState, PlaybackState>(this).RegisterCommand<Seek, PlaybackState>(this)
            .RegisterCommand<Transport, PlaybackState>(this).RegisterCommand<EnqueueTracks, PlaybackState>(this)
            .RegisterCommand<CueDeck, PlaybackState>(this).RegisterCommand<SetQueuePlaybackOptions, PlaybackState>(this)
            .RegisterCommand<LoadPlaylistQueue, PlaybackState>(this).RegisterCommand<EditQueue, PlaybackState>(this)
            .RegisterCommand<SetQueueAutoSave, PlaybackState>(this);

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
        var deckId = command.DeckId ?? primaryDeck;
        if (!Enum.IsDefined(deckId) || command.ActiveTrackId == Guid.Empty || command.Position < TimeSpan.Zero)
            return new Outcome<PlaybackState>.Invalid([new("invalid-playback-seek")]);

        var deck = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        var track = command.DeckId.HasValue ? deck?.TrackId : state.ActiveTrackId;
        if (track != command.ActiveTrackId || deck is not null && (deck.TrackId != track || !deck.Ready))
            return new Outcome<PlaybackState>.Conflict("seek-track-changed", state.Revision);
        var duration = deck?.Duration ?? (deckId == primaryDeck ? state.Duration : null);
        if (duration is { } end && command.Position > end)
            return new Outcome<PlaybackState>.Invalid([new("seek-position-out-of-range", nameof(command.Position))]);

        var outcome = await backend.SeekAsync(deckId, command.ActiveTrackId, command.Position, token).ConfigureAwait(false);
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

    private Task<Outcome<PlaybackState>> CueAsync(CueDeck command, CancellationToken token) =>
        !CanTransport() || !state.Capabilities.HasFlag(PlaybackCapabilities.Mixer)
            ? Task.FromResult(Invalid("playback-cue-unsupported"))
            : !Enum.IsDefined(command.DeckId) || command.TrackId == Guid.Empty
                ? Task.FromResult(Invalid("invalid-playback-cue"))
                : ChangeAsync(command.DeckId, command.TrackId, TimeSpan.Zero, TransportStatus.Stopped,
                    state.ActiveTrackId == command.TrackId ? state.ActiveQueueEntryId : null, false, token);

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
        var deckId = command.DeckId ?? primaryDeck;
        if (!Enum.IsDefined(command.Action) || !Enum.IsDefined(deckId))
            return Invalid("invalid-transport-request");
        if (command.Action is TransportAction.Next or TransportAction.Previous)
        {
            if (command.DeckId.HasValue)
                return Invalid("queue-navigation-requires-primary-transport");
            var entry = AdjacentEntry(command.Action == TransportAction.Next ? 1 : -1);
            return entry is null
                ? new Outcome<PlaybackState>.Success(state)
                : await StartEntryAsync(entry, TimeSpan.Zero, token).ConfigureAwait(false);
        }

        var status = command.Action switch
        {
            TransportAction.Play => TransportStatus.Playing,
            TransportAction.Pause => TransportStatus.Paused,
            _ => TransportStatus.Stopped
        };
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

    private Task<Outcome<PlaybackState>> StartEntryAsync(QueueEntry entry, TimeSpan position, CancellationToken token) =>
        ChangeAsync(primaryDeck, entry.TrackId, position, TransportStatus.Playing, entry.EntryId, true, token);

    private async Task<Outcome<PlaybackState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status,
        Guid? queueEntry, bool resetFade, CancellationToken token)
    {
        var target = (IPlaybackTransportTarget)backend;
        var result = await target.ChangeAsync(deck, track, position, status, token).ConfigureAwait(false);
        return await ApplyDeckResultAsync(result, deck, track, queueEntry, resetFade).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> ApplyDeckResultAsync(Outcome<DeckState> result, DeckId deckId, Guid track, Guid? queueEntry, bool resetFade)
    {
        if (result is not Outcome<DeckState>.Success success)
            return CopyFailure(result);
        var applied = success.Value;
        if (!MatchesDeck(applied, deckId, track))
            return new Outcome<PlaybackState>.DependencyFailure("invalid-playback-deck-receipt", false);

        var previous = state.Decks.FirstOrDefault(item => item.DeckId == deckId);
        var next = state with
        {
            Decks = previous is null ? state.Decks.Add(applied) : state.Decks.Replace(previous, applied),
            Output = resetFade && state.Output is { FadeActive: true } output ? output with { FadeActive = false } : state.Output
        };
        if (deckId != primaryDeck)
        {
            state = next with { Revision = checked(state.Revision + 1) };
            return new Outcome<PlaybackState>.Success(state);
        }
        return await CommitAsync(next with { ActiveTrackId = track, ActiveQueueEntryId = queueEntry,
            Position = applied.Position, Duration = applied.Duration, Status = applied.Status }, true, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Outcome<PlaybackState>> ReadLiveStateAsync(CancellationToken token)
    {
        if (backend is not IPlaybackTransportTarget target)
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
            if (deck.DeckId == primaryDeck)
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
            var write = await checkpointStore.SaveCheckpointAsync(checkpoint, checkpointRevision, token).ConfigureAwait(false);
            if (write is not Outcome<PlaybackCheckpoint>.Success saved)
                return CopyFailure(write);
            checkpointRevision = saved.Value.CheckpointRevision;
        }
        checkpointPending = false;
        return new Outcome<PlaybackState>.Success(next);
    }

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
                playlistBinding = saved.PlaylistBacking;
                state = state with
                {
                    Queue = state.Queue with { Entries = saved.Queue, QueueRevision = saved.QueueRevision, Shuffle = saved.Shuffle, Repeat = saved.Repeat,
                        BackingPlaylist = playlistBinding?.Version, AutoSave = playlistBinding is not null && initialAutoSave,
                        SaveStatus = QueueSaveStatus.NotRequested, SaveReceipt = null },
                    ActiveQueueEntryId = saved.ActiveEntryId,
                    ActiveTrackId = saved.Queue.FirstOrDefault(entry => entry.EntryId == saved.ActiveEntryId)?.TrackId,
                    Position = saved.Position, Duration = null, Status = TransportStatus.Stopped,
                    Decks = [.. state.Decks.Where(deck => deck.DeckId != primaryDeck)]
                };
                await RefreshSaveStatusAsync(token).ConfigureAwait(false);
            }
        }
        restored = true;
        return new Outcome<PlaybackState>.Success(state);
    }

    private Task<Outcome<PlaybackState>> RunCommandAsync(Guid sessionId, object intent, CommandContext context,
        Func<CancellationToken, Task<Outcome<PlaybackState>>> operation, CancellationToken token) => RunAsync(sessionId, async ct =>
    {
        if (pendingEffect is { } effect && effect.OperationId == context.OperationId && !Equals(effect.Intent, intent))
            return new Outcome<PlaybackState>.Conflict("playback-operation-reused", state.Revision);
        if (checkpointPending)
        {
            var persisted = await PersistAsync(state, ct).ConfigureAwait(false);
            if (persisted is not Outcome<PlaybackState>.Success)
                return persisted;
        }
        if (pendingEffect is { } completed && completed.OperationId == context.OperationId && completed.Revision == state.Revision)
            return new Outcome<PlaybackState>.Success(state);
        var before = state.Revision;
        var result = await operation(ct).ConfigureAwait(false);
        if (state.Revision != before)
            pendingEffect = checkpointPending ? (context.OperationId, intent, state.Revision) : null;
        return result;
    }, token);

    private bool CanTransport() => backend is IPlaybackTransportTarget && state.Capabilities.HasFlag(PlaybackCapabilities.Transport);

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
