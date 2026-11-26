using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Replication;

/// <summary>Reads the registered host's exact catalog pages and acknowledges a committed checkpoint.</summary>
/// <remarks>2026-11-24: Define the incremental transport port for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
public interface ICatalogReplicaHost
{
    Task<Outcome<CatalogFeedPage>> ReadChangesAsync(long after, long? upper, CancellationToken token);
    Task<Outcome<CatalogAcknowledgementReceipt>> AcknowledgeAsync(long through, CancellationToken token);
    /// <summary>Begins or reopens the operator's pinned replacement capture.</summary>
    Task<Outcome<CatalogSnapshotPage>> BeginSnapshotAsync(Guid snapshotId, long expectedCursor, CancellationToken token);
    /// <summary>Reads the next ordered page of the pinned replacement.</summary>
    Task<Outcome<CatalogSnapshotPage>> ReadSnapshotAsync(Guid snapshotId, int offset, CancellationToken token);
}

/// <summary>Applies an entire versioned page and its cursor as one local transaction.</summary>
/// <remarks>2026-11-24: Define persistence for FR-RECORDS-001#ac-replicate without rewriting local drafts.</remarks>
public interface ICatalogReplicaStore
{
    Task<Outcome<ReplicationState>> ReadReplicaAsync(Guid library, Guid instance, CancellationToken token);
    Task<Outcome<ReplicationState>> ApplyCatalogPageAsync(CatalogFeedPage page, CancellationToken token);
    Task<Outcome<ReplicationState>> RequireCatalogSnapshotAsync(Guid library, Guid instance, long cursor, CancellationToken token);
    /// <summary>Stages the next bounded page and replaces the live projection only when it is complete.</summary>
    Task<Outcome<ReplicationState>> ApplySnapshotPageAsync(CatalogSnapshotPage page, long expectedCursor, CancellationToken token);
}

/// <summary>Resumes a contiguous authenticated feed from the persisted desktop cursor.</summary>
/// <remarks>2026-11-24: Bind the existing replica commands to incremental pull and acknowledge only after local commit for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
/// <remarks>2026-11-24: Recover expired cursors without rewriting local drafts for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
/// <remarks>2026-11-25: Serialize automatic refresh with manual recovery and acknowledge each confirmed cursor for FR-RECORDS-001#ac-notify.</remarks>
public sealed class CatalogReplicaHandlers(Guid library, Guid instance, ICatalogReplicaHost host, ICatalogReplicaStore store,
    LibraryRecordHandlers? records = null) :
    IQueryHandler<GetReplicationStatus, ReplicationState>, ICommandHandler<ResumeReplication, ReplicationState>,
    ICommandHandler<RefreshReplication, ReplicationState>
{
    private readonly SemaphoreSlim resumeLane = new(1, 1);
    private long acknowledgedThrough;

    /// <summary>Registers the replica status and recovery requests on the typed dispatcher.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder)
    {
        builder.RegisterQuery<GetReplicationStatus, ReplicationState>(this).RegisterCommand<ResumeReplication, ReplicationState>(this)
            .RegisterCommand<RefreshReplication, ReplicationState>(this);
        return records?.Register(builder) ?? builder;
    }

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> HandleAsync(GetReplicationStatus query, RequestContext context, CancellationToken token) =>
        Authorized(query.LibraryId, query.InstanceId, context) ? store.ReadReplicaAsync(library, instance, token) : Task.FromResult(Rejected());

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> HandleAsync(ResumeReplication command, CommandContext context, CancellationToken token)
    {
        if (!Authorized(command.LibraryId, command.InstanceId, context.Request) || command.ExpectedCursor < 0 || context.OperationId == Guid.Empty)
            return Task.FromResult(Rejected());
        if (!Enum.IsDefined(command.RecoveryMode))
            return Task.FromResult(Rejected());
        return OnLaneAsync(command.RecoveryMode == ReplicaRecoveryMode.Snapshot
            ? () => ReplaceSnapshotAsync(command.ExpectedCursor, context.OperationId, token)
            : () => ResumeAsync(command.ExpectedCursor, context.OperationId, token), token);
    }

    /// <inheritdoc />
    public Task<Outcome<ReplicationState>> HandleAsync(RefreshReplication command, CommandContext context, CancellationToken token) =>
        Authorized(command.LibraryId, command.InstanceId, context.Request) && context.OperationId != Guid.Empty
            ? OnLaneAsync(() => ResumeAsync(null, null, token), token) : Task.FromResult(Rejected());

    private async Task<Outcome<ReplicationState>> OnLaneAsync(Func<Task<Outcome<ReplicationState>>> refresh, CancellationToken token)
    {
        await resumeLane.WaitAsync(token).ConfigureAwait(false);
        try { return await refresh().ConfigureAwait(false); }
        finally { resumeLane.Release(); }
    }

    private async Task<Outcome<ReplicationState>> ResumeAsync(long? expectedCursor, Guid? operation, CancellationToken token)
    {
        var current = await store.ReadReplicaAsync(library, instance, token).ConfigureAwait(false);
        if (current is not Outcome<ReplicationState>.Success { Value: var state })
            return current;
        if (operation is null && state.SnapshotRequired)
            return current;
        // An older request can safely resume forward after the desktop committed a page and lost its acknowledgement.
        if (expectedCursor > state.Cursor)
            return new Outcome<ReplicationState>.Conflict("stale-replica-cursor", state.Cursor);
        var replay = await ReplayRecordsAsync(token).ConfigureAwait(false);
        if (replay is not Outcome<bool>.Success)
            return Failure(replay);
        long? upper = null;
        do
        {
            var pulled = await host.ReadChangesAsync(state.Cursor, upper, token).ConfigureAwait(false);
            if (pulled is Outcome<CatalogFeedPage>.Conflict { Code: "catalog-snapshot-required" })
                return await store.RequireCatalogSnapshotAsync(library, instance, state.Cursor, token).ConfigureAwait(false);
            if (pulled is not Outcome<CatalogFeedPage>.Success { Value: var page })
                return Failure(pulled);
            if (!CatalogFeedProtocol.IsValid(page) || page.LibraryId != library || page.RecipientInstanceId != instance ||
                page.AfterRevision != state.Cursor || (upper.HasValue && upper != page.UpperRevision))
                return new Outcome<ReplicationState>.DependencyFailure("catalog-page-mismatch", true);
            upper = page.UpperRevision;
            current = await store.ApplyCatalogPageAsync(page, token).ConfigureAwait(false);
            if (current is not Outcome<ReplicationState>.Success { Value: var applied })
                return current;
            state = applied;
        } while (state.Cursor < upper);

        return await AcknowledgeCommittedAsync(state, operation, token).ConfigureAwait(false);
    }

    private async Task<Outcome<ReplicationState>> ReplaceSnapshotAsync(long expectedCursor, Guid snapshot, CancellationToken token)
    {
        var current = await store.ReadReplicaAsync(library, instance, token).ConfigureAwait(false);
        if (current is not Outcome<ReplicationState>.Success { Value: var state })
            return current;
        if (state.RecoveryOperationId == snapshot)
            return await AcknowledgeCommittedAsync(state, snapshot, token).ConfigureAwait(false);
        if (state.Cursor != expectedCursor)
            return new Outcome<ReplicationState>.Conflict("stale-replica-cursor", state.Cursor);
        var replay = await ReplayRecordsAsync(token).ConfigureAwait(false);
        if (replay is not Outcome<bool>.Success)
            return Failure(replay);

        var result = await host.BeginSnapshotAsync(snapshot, expectedCursor, token).ConfigureAwait(false);
        long? capturedRevision = null;
        int? total = null;
        var offset = 0;
        while (result is Outcome<CatalogSnapshotPage>.Success { Value: var page })
        {
            if (!CatalogSnapshotProtocol.IsValid(page) || page.LibraryId != library || page.RecipientInstanceId != instance ||
                page.SnapshotId != snapshot || page.UpperRevision < expectedCursor || page.Offset != offset ||
                (capturedRevision.HasValue && (capturedRevision != page.UpperRevision || total != page.TotalCount)))
                return new Outcome<ReplicationState>.DependencyFailure("catalog-snapshot-page-mismatch", true);
            capturedRevision = page.UpperRevision;
            total = page.TotalCount;
            current = await store.ApplySnapshotPageAsync(page, expectedCursor, token).ConfigureAwait(false);
            if (current is not Outcome<ReplicationState>.Success { Value: var replaced })
                return current;
            if (page.NextOffset == total)
                return replaced.Cursor == capturedRevision && replaced.RecoveryOperationId == snapshot
                    ? await AcknowledgeCommittedAsync(replaced, snapshot, token).ConfigureAwait(false)
                    : new Outcome<ReplicationState>.DependencyFailure("catalog-snapshot-commit-mismatch", true);
            offset = page.NextOffset;
            result = await host.ReadSnapshotAsync(snapshot, offset, token).ConfigureAwait(false);
        }
        return Failure(result);
    }

    private async Task<Outcome<ReplicationState>> AcknowledgeCommittedAsync(ReplicationState state, Guid? operation, CancellationToken token)
    {
        if (state.Cursor > 0 && (operation.HasValue || state.Cursor > acknowledgedThrough))
        {
            var ack = await host.AcknowledgeAsync(state.Cursor, token).ConfigureAwait(false);
            if (ack is not Outcome<CatalogAcknowledgementReceipt>.Success { Value: var receipt })
                return Failure(ack);
            if (receipt.LibraryId != library || receipt.RecipientInstanceId != instance || receipt.ThroughRevision != state.Cursor)
                return new Outcome<ReplicationState>.DependencyFailure("catalog-ack-mismatch", true);
            acknowledgedThrough = state.Cursor;
        }
        return new Outcome<ReplicationState>.Success(operation is { } id ? state with { RecoveryOperationId = id } : state);
    }

    private bool Authorized(Guid scope, Guid origin, RequestContext context) => scope == library && origin == instance &&
        library != Guid.Empty && instance != Guid.Empty && context.OriginInstanceId == instance;

    /// <summary>Flushes persisted listener intents before capturing the receiving revision window.</summary>
    /// <remarks>2026-11-25: Replay during reconnect for FR-RECORDS-001#ac-offline.</remarks>
    private Task<Outcome<bool>> ReplayRecordsAsync(CancellationToken token) => records is null
        ? Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true)) : records.Scope == new LibraryRecordScope(library, instance)
            ? records.ReplayAsync(token) : Task.FromResult<Outcome<bool>>(new Outcome<bool>.Invalid([new("replica-record-scope-mismatch")]));

    private static Outcome<ReplicationState> Rejected() => new Outcome<ReplicationState>.Invalid([new("replica-scope-unavailable")]);

    private static Outcome<ReplicationState> Failure<T>(Outcome<T> outcome) => outcome switch
    {
        Outcome<T>.Invalid invalid => new Outcome<ReplicationState>.Invalid(invalid.Issues),
        Outcome<T>.Conflict conflict => new Outcome<ReplicationState>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<T>.Cancelled => new Outcome<ReplicationState>.Cancelled(),
        Outcome<T>.DependencyFailure failure => new Outcome<ReplicationState>.DependencyFailure(failure.Code, failure.Retryable),
        _ => throw new InvalidOperationException("Expected a failed replica operation.")
    };
}
