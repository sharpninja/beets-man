using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Replication;

/// <summary>Identifies the library and desktop whose unpublished operations are local.</summary>
public readonly record struct LibraryRecordScope(Guid LibraryId, Guid OriginInstanceId);

/// <summary>Records an immutable play for the scoped library and playback occurrence.</summary>
/// <remarks>2026-11-25: Persist the stable play ID for FR-RECORDS-001#ac-offline.</remarks>
public sealed record RecordPlay(Guid PlayId, Guid TrackId, DateTimeOffset PlayedAtUtc) : ICommand<RecordedPlay>;

/// <summary>Returns the stable local play and its delivery status.</summary>
public sealed record RecordedPlay(Guid PlayId, Guid TrackId, DateTimeOffset PlayedAtUtc, bool PendingSync);

/// <summary>Reads recent confirmed or locally pending history in time order.</summary>
public sealed record GetListeningHistory(int Limit) : IQuery<ImmutableArray<RecordedPlay>>;

/// <summary>Contains the durable request the desktop is ready to send or safely retry verbatim.</summary>
public sealed record LibraryRecordAttempt(Guid OperationId, Guid RecordId, LibraryRecordIntent Change);

/// <summary>Owns the local intent journal, its effective projection, and conflict decisions.</summary>
/// <remarks>2026-11-25: Keep confirmed rows separate from offline drafts for FR-RECORDS-001#ac-offline.</remarks>
public interface ILibraryRecordOutboxStore
{
    Task<Outcome<FavoriteState>> ReadFavoriteAsync(LibraryRecordScope scope, Guid track, CancellationToken token);
    Task<Outcome<ImmutableArray<RecordedPlay>>> ReadHistoryAsync(LibraryRecordScope scope, int limit, CancellationToken token);
    Task<Outcome<FavoriteState>> StageFavoriteAsync(LibraryRecordScope scope, Guid operation, SetFavorite command, CancellationToken token);
    Task<Outcome<RecordedPlay>> StagePlayAsync(LibraryRecordScope scope, Guid operation, RecordPlay command, CancellationToken token);
    Task<Outcome<ImmutableArray<Guid>>> ReadPendingRecordOperationsAsync(LibraryRecordScope scope, CancellationToken token);
    Task<Outcome<LibraryRecordAttempt?>> PrepareRecordAsync(LibraryRecordScope scope, Guid operation, CancellationToken token);
    Task<Outcome<bool>> AcceptRecordAsync(LibraryRecordScope scope, LibraryRecordAttempt attempt, LibraryRecordReceipt receipt, CancellationToken token);
    Task<Outcome<bool>> KeepRecordConflictAsync(LibraryRecordScope scope, LibraryRecordAttempt attempt, string code, long? actualRevision, CancellationToken token);
    Task<Outcome<ReplicaConflictReceipt>> ResolveRecordConflictAsync(LibraryRecordScope scope, Guid operation, ResolveReplicaConflict request, CancellationToken token);
}

/// <summary>Stages offline listener actions and replays their original operations at reconnect.</summary>
/// <remarks>2026-11-25: Keep conflicting favorites available for reviewed resolution for FR-RECORDS-001#ac-offline.</remarks>
public sealed class LibraryRecordHandlers(LibraryRecordScope scope, ILibraryRecordOutboxStore store, ILibraryRecordHost host) :
    IQueryHandler<GetFavorite, FavoriteState>, ICommandHandler<SetFavorite, FavoriteState>,
    IQueryHandler<GetListeningHistory, ImmutableArray<RecordedPlay>>, ICommandHandler<RecordPlay, RecordedPlay>,
    ICommandHandler<ResolveReplicaConflict, ReplicaConflictReceipt>
{
    private readonly SemaphoreSlim replayLane = new(1, 1);
    internal LibraryRecordScope Scope => scope;

    /// <summary>Registers the listener commands and the existing conflict resolution command.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetFavorite, FavoriteState>(this).RegisterCommand<SetFavorite, FavoriteState>(this)
        .RegisterQuery<GetListeningHistory, ImmutableArray<RecordedPlay>>(this).RegisterCommand<RecordPlay, RecordedPlay>(this)
        .RegisterCommand<ResolveReplicaConflict, ReplicaConflictReceipt>(this);

    /// <inheritdoc />
    public Task<Outcome<FavoriteState>> HandleAsync(GetFavorite query, RequestContext context, CancellationToken token) =>
        Allows(context) && query.TrackId != Guid.Empty ? store.ReadFavoriteAsync(scope, query.TrackId, token) : Invalid<FavoriteState>();

    /// <inheritdoc />
    public Task<Outcome<FavoriteState>> HandleAsync(SetFavorite command, CommandContext context, CancellationToken token) =>
        Allows(context.Request) && context.OperationId != Guid.Empty && command.TrackId != Guid.Empty && command.ExpectedRevision >= 0
            ? store.StageFavoriteAsync(scope, context.OperationId, command, token) : Invalid<FavoriteState>();

    /// <inheritdoc />
    public Task<Outcome<ImmutableArray<RecordedPlay>>> HandleAsync(GetListeningHistory query, RequestContext context, CancellationToken token) =>
        Allows(context) && query.Limit is >= 1 and <= 100 ? store.ReadHistoryAsync(scope, query.Limit, token) : Invalid<ImmutableArray<RecordedPlay>>();

    /// <inheritdoc />
    public Task<Outcome<RecordedPlay>> HandleAsync(RecordPlay command, CommandContext context, CancellationToken token) =>
        Allows(context.Request) && context.OperationId != Guid.Empty && command.PlayId != Guid.Empty && command.TrackId != Guid.Empty && command.PlayedAtUtc.UtcTicks > 0
            ? store.StagePlayAsync(scope, context.OperationId, command with { PlayedAtUtc = command.PlayedAtUtc.ToUniversalTime() }, token) : Invalid<RecordedPlay>();

    /// <inheritdoc />
    public Task<Outcome<ReplicaConflictReceipt>> HandleAsync(ResolveReplicaConflict command, CommandContext context, CancellationToken token) =>
        Allows(context.Request) && context.OperationId != Guid.Empty && command.ConflictId != Guid.Empty && command.ExpectedHostRevision >= 0 && Enum.IsDefined(command.Resolution)
            ? store.ResolveRecordConflictAsync(scope, context.OperationId, command, token) : Invalid<ReplicaConflictReceipt>();

    /// <summary>Publishes ready local intents without rewriting an operation after an uncertain response.</summary>
    public async Task<Outcome<bool>> ReplayAsync(CancellationToken token)
    {
        await replayLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pending = await store.ReadPendingRecordOperationsAsync(scope, token).ConfigureAwait(false);
            if (pending is not Outcome<ImmutableArray<Guid>>.Success { Value: var operations })
                return LibraryBatchHandlers.CopyFailure<ImmutableArray<Guid>, bool>(pending);
            foreach (var operation in operations)
            {
                var prepared = await store.PrepareRecordAsync(scope, operation, token).ConfigureAwait(false);
                if (prepared is not Outcome<LibraryRecordAttempt?>.Success { Value: var attempt })
                    return LibraryBatchHandlers.CopyFailure<LibraryRecordAttempt?, bool>(prepared);
                if (attempt is null)
                    continue;

                var response = await host.PublishAsync(attempt.Change, operation, token).ConfigureAwait(false);
                var saved = response switch
                {
                    Outcome<LibraryRecordReceipt>.Success success => store.AcceptRecordAsync(scope, attempt, success.Value, CancellationToken.None),
                    Outcome<LibraryRecordReceipt>.Conflict conflict => store.KeepRecordConflictAsync(scope, attempt, conflict.Code, conflict.ActualRevision, CancellationToken.None),
                    Outcome<LibraryRecordReceipt>.Invalid invalid => store.KeepRecordConflictAsync(scope, attempt, invalid.Issues.FirstOrDefault()?.Code ?? "invalid-library-record", null, CancellationToken.None),
                    _ => null
                };
                if (saved is null)
                    return response is Outcome<LibraryRecordReceipt>.Cancelled ? new Outcome<bool>.Cancelled() : new Outcome<bool>.Success(false);
                if (await saved.ConfigureAwait(false) is not Outcome<bool>.Success { Value: true })
                    return new Outcome<bool>.DependencyFailure("library-record-result-unavailable", true);
            }
            return new Outcome<bool>.Success(true);
        }
        finally { replayLane.Release(); }
    }

    private bool Allows(RequestContext context) => scope.LibraryId != Guid.Empty && scope.OriginInstanceId != Guid.Empty &&
        scope.OriginInstanceId == context.OriginInstanceId;

    private static Task<Outcome<T>> Invalid<T>() => Task.FromResult<Outcome<T>>(new Outcome<T>.Invalid([new("invalid-local-library-record")]));
}
