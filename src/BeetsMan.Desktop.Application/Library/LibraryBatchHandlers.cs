using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Persistence;

namespace BeetsMan.Desktop.Application.Library;

/// <summary>Owns the originating desktop's batch review, staging, resolution, and replay checkpoints.</summary>
/// <remarks>2026-11-24: Implement the local operation state machine for FR-LIBRARY-002 and TR-LIBRARY-ARCH-002.</remarks>
public sealed partial class LibraryBatchHandlers(LibraryBatchScope scope, IWriteUnitOfWork<ILibraryBatchRepository> store,
    IApprovedLibraryBatchPublisher publisher, ILibraryBatchImportFiles? importFiles = null) : IQueryHandler<GetLibraryBatchMode, LibraryBatchMode>,
    IQueryHandler<GetLibraryBatchReview, LibraryBatchReview>, ICommandHandler<SetLibraryBatchMode, LibraryBatchMode>,
    ICommandHandler<CommitLibraryBatch, LibraryBatchCommitReceipt>, ICommandHandler<ResumeLibraryBatchCommit, LibraryBatchCommitReceipt>,
    ICommandHandler<RevertLibraryBatch, LibraryBatchMode>, IQueryHandler<GetLibraryBatchImportReviews, LibraryBatchImportReviews>,
    ICommandHandler<ChooseLibraryBatchImportAction, LibraryBatchImportChoiceReceipt>
{
    private readonly SemaphoreSlim publicationLane = new(1, 1);

    /// <summary>Registers the typed control and review requests for this library and local origin.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetLibraryBatchMode, LibraryBatchMode>(this)
        .RegisterQuery<GetLibraryBatchReview, LibraryBatchReview>(this)
        .RegisterQuery<GetLibraryBatchImportReviews, LibraryBatchImportReviews>(this)
        .RegisterCommand<SetLibraryBatchMode, LibraryBatchMode>(this)
        .RegisterCommand<CommitLibraryBatch, LibraryBatchCommitReceipt>(this)
        .RegisterCommand<ResumeLibraryBatchCommit, LibraryBatchCommitReceipt>(this)
        .RegisterCommand<ChooseLibraryBatchImportAction, LibraryBatchImportChoiceReceipt>(this)
        .RegisterCommand<RevertLibraryBatch, LibraryBatchMode>(this);

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchMode>> HandleAsync(GetLibraryBatchMode query, RequestContext context, CancellationToken token) =>
        Authorized(context) ? store.ExecuteAsync(repository =>
        {
            var mode = repository.Read(scope);
            return Success(ProjectMode(mode) with { ImportCleanupPending = ImportCopiesToRelease(repository, mode).Any() });
        }, token) : Denied<LibraryBatchMode>();

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchReview>> HandleAsync(GetLibraryBatchReview query, RequestContext context, CancellationToken token) =>
        Authorized(context) ? store.ExecuteAsync(Review, token) : Denied<LibraryBatchReview>();

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchMode>> HandleAsync(SetLibraryBatchMode command, CommandContext context, CancellationToken token) =>
        Authorized(context.Request) ? OnMutationLaneAsync(() => store.ExecuteAsync(repository => SetMode(repository, command), token), token) : Denied<LibraryBatchMode>();

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchMode>> HandleAsync(RevertLibraryBatch command, CommandContext context, CancellationToken token) =>
        Authorized(context.Request) ? RevertAndReleaseCopiesAsync(command, token) : Denied<LibraryBatchMode>();

    /// <summary>Records one prepared mutation while the mode is active. The operation can never replay outside its batch.</summary>
    /// <remarks>2026-11-24: Keep prepared library changes local for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
    public Task<Outcome<LibraryBatchStageReceipt>> StageAsync(LibraryBatchChange change, CommandContext context, CancellationToken token)
    {
        if (!Authorized(context.Request))
            return Denied<LibraryBatchStageReceipt>();
        return OnMutationLaneAsync(() => StoreStageAsync(change, context.OperationId, token), token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchCommitReceipt>> HandleAsync(CommitLibraryBatch command, CommandContext context, CancellationToken token)
    {
        if (!Authorized(context.Request))
            return Denied<LibraryBatchCommitReceipt>();
        if (context.OperationId == Guid.Empty || command.Approval.ReviewId == Guid.Empty)
            return Task.FromResult(Invalid<LibraryBatchCommitReceipt>("library-batch-approval-required"));
        return SubmitCommitAsync(new(context.OperationId, command), token);
    }

    /// <inheritdoc />
    public async Task<Outcome<LibraryBatchCommitReceipt>> HandleAsync(ResumeLibraryBatchCommit command, CommandContext context, CancellationToken token)
    {
        if (!Authorized(context.Request))
            return Invalid<LibraryBatchCommitReceipt>("library-batch-origin-unavailable");
        var saved = await store.ExecuteAsync(repository => Success(repository.Read(scope).Seal), token).ConfigureAwait(false);
        if (saved is not Outcome<LibraryBatchSeal?>.Success { Value: var seal })
            return CopyFailure<LibraryBatchSeal?, LibraryBatchCommitReceipt>(saved);
        return seal is null ? Invalid<LibraryBatchCommitReceipt>("library-batch-no-pending-commit") :
            await SubmitCommitAsync(seal, token).ConfigureAwait(false);
    }

    private async Task<Outcome<LibraryBatchCommitReceipt>> SubmitCommitAsync(LibraryBatchSeal seal, CancellationToken token)
    {
        await publicationLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var prepared = await OnMutationLaneAsync(() => store.ExecuteAsync(repository => PrepareCommit(repository, seal), token), token).ConfigureAwait(false);
            if (prepared is not Outcome<CommitPreparation>.Success { Value: var ready })
                return CopyFailure<CommitPreparation, LibraryBatchCommitReceipt>(prepared);
            if (ready.Committed is { } committed)
                return Success(committed);

            var result = await publisher.PublishAsync(scope, ready.Review!, seal.OperationId, token).ConfigureAwait(false);
            if (result is Outcome<LibraryBatchCommitReceipt>.Success { Value: var receipt })
            {
                var completed = await OnMutationLaneAsync(() => store.ExecuteAsync(repository => FinalizeCommit(repository, seal, receipt),
                    CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
                if (completed is Outcome<LibraryBatchCommitReceipt>.Success && importFiles is not null)
                    await RecoverPrivateImportCopiesAsync(CancellationToken.None).ConfigureAwait(false);
                return completed;
            }
            if (result is Outcome<LibraryBatchCommitReceipt>.Invalid or Outcome<LibraryBatchCommitReceipt>.Conflict)
            {
                var released = await store.ExecuteAsync(repository => ReleaseRejectedSeal(repository, seal), CancellationToken.None).ConfigureAwait(false);
                if (released is not Outcome<bool>.Success)
                    return CopyFailure<bool, LibraryBatchCommitReceipt>(released);
            }
            return result;
        }
        finally { publicationLane.Release(); }
    }

    private Outcome<LibraryBatchMode> SetMode(ILibraryBatchRepository repository, SetLibraryBatchMode command)
    {
        var current = repository.Read(scope);
        if (current.Revision != command.ExpectedRevision)
            return Conflict<LibraryBatchMode>("stale-library-batch-mode", current.Revision);
        if (command.Enabled == current.BatchId.HasValue)
            return Success(ProjectMode(current));
        if (current.ChangeCount != 0)
            return Conflict<LibraryBatchMode>("library-batch-resolution-required", current.Revision);
        var next = EmptyBatch(current, command.Enabled);
        repository.Save(scope, next);
        return Success(ProjectMode(next));
    }

    private Outcome<LibraryBatchReview> Review(ILibraryBatchRepository repository)
    {
        var current = repository.Read(scope);
        if (current.BatchId is not { } batchId)
            return Invalid<LibraryBatchReview>("library-batch-mode-disabled");
        return Success(ProjectReview(repository, current, batchId));
    }

    private Outcome<LibraryBatchStageReceipt> Stage(ILibraryBatchRepository repository, LibraryBatchChange change, Guid operationId, string fingerprint)
    {
        var current = repository.Read(scope);
        var staged = repository.FindStage(scope, operationId);
        if (repository.IsDirect(scope, operationId) || repository.FindCommit(scope, operationId) is not null || (staged is not null && staged.Fingerprint != fingerprint))
            return Conflict<LibraryBatchStageReceipt>("operation-intent-mismatch", current.Revision);
        if (staged is not null)
            return staged.Receipt.Version.BatchId == current.BatchId ? Success(staged.Receipt) :
                Conflict<LibraryBatchStageReceipt>("library-batch-closed", current.Revision);
        if (current.BatchId is not { } batchId)
            return Invalid<LibraryBatchStageReceipt>("library-batch-mode-disabled");
        if (current.Seal is not null)
            return Conflict<LibraryBatchStageReceipt>("library-batch-commit-in-progress", current.Revision);

        var revision = checked(current.Revision + 1);
        var position = checked(current.ChangeCount + 1);
        var receipt = new LibraryBatchStageReceipt(operationId, new LibraryBatchVersion(batchId, revision), position);
        repository.AddStage(scope, new(receipt, fingerprint), new(operationId, position, change));
        repository.Save(scope, current with { Revision = revision, ChangeCount = position, ReviewId = Guid.NewGuid() });
        return Success(receipt);
    }

    private Outcome<LibraryBatchMode> Revert(ILibraryBatchRepository repository, RevertLibraryBatch command, LibraryBatchSeal? expectedSeal)
    {
        var current = repository.Read(scope);
        if (current.BatchId != command.Version.BatchId || current.Revision != command.Version.Revision)
            return Conflict<LibraryBatchMode>("stale-library-batch-review", current.Revision);
        if (current.Seal != expectedSeal)
            return Conflict<LibraryBatchMode>("library-batch-commit-in-progress", current.Revision);
        repository.RemoveEntries(scope, command.Version.BatchId);
        var next = EmptyBatch(current, command.KeepModeEnabled);
        repository.Save(scope, next);
        return Success(ProjectMode(next));
    }

    private Outcome<CommitPreparation> PrepareCommit(ILibraryBatchRepository repository, LibraryBatchSeal seal)
    {
        var current = repository.Read(scope);
        if (repository.FindCommit(scope, seal.OperationId) is { } prior)
            return prior.Seal == seal ? Success(new CommitPreparation(null, prior.Receipt)) :
                Conflict<CommitPreparation>("operation-intent-mismatch", current.Revision);
        if (repository.IsDirect(scope, seal.OperationId) || repository.FindStage(scope, seal.OperationId) is not null)
            return Conflict<CommitPreparation>("operation-intent-mismatch", current.Revision);
        if (current.Seal is { } pending && pending != seal)
            return Conflict<CommitPreparation>("library-batch-commit-in-progress", current.Revision);

        var approval = seal.Command.Approval;
        if (current.BatchId != approval.Version.BatchId || current.Revision != approval.Version.Revision || current.ReviewId != approval.ReviewId)
            return Conflict<CommitPreparation>("stale-library-batch-review", current.Revision);
        if (current.ChangeCount == 0)
            return Invalid<CommitPreparation>("library-batch-empty");
        if (current.Seal is null)
            repository.Save(scope, current with { Seal = seal });
        return Success(new CommitPreparation(ProjectReview(repository, current, approval.Version.BatchId), null));
    }

    /// <summary>Settles the persisted approval only after validating the complete publication receipt.</summary>
    /// <remarks>2026-11-26: Retain the seal and all reviewed entries on mismatched receipts for FR-LIBRARY-002#ac-batch-commit.</remarks>
    private Outcome<LibraryBatchCommitReceipt> FinalizeCommit(ILibraryBatchRepository repository, LibraryBatchSeal seal, LibraryBatchCommitReceipt receipt)
    {
        if (repository.FindCommit(scope, seal.OperationId) is { } prior && prior.Seal == seal)
            return Success(prior.Receipt);
        var current = repository.Read(scope);
        if (current.Seal != seal)
            return Conflict<LibraryBatchCommitReceipt>("library-batch-commit-changed", current.Revision);

        var approval = seal.Command.Approval;
        if (receipt is null || receipt.LibraryId != scope.LibraryId || receipt.OperationId != seal.OperationId ||
            receipt.Version != approval.Version || current.ReviewId != approval.ReviewId ||
            current.BatchId != approval.Version.BatchId || current.Revision != approval.Version.Revision)
            return ReceiptMismatch();

        var entries = ProjectReview(repository, current, approval.Version.BatchId).Entries;
        var playlistEntries = entries.Where(entry => entry.Change is LibraryBatchChange.Playlist).ToArray();
        var catalogBaseline = entries.Max(entry => entry.Change switch
        {
            LibraryBatchChange.TrackTags tags => tags.ExpectedRevision,
            LibraryBatchChange.Organize organize => organize.Plan.CatalogRevision,
            LibraryBatchChange.Remove remove => remove.Plan.CatalogRevision,
            _ => 0L
        });
        if (receipt.CatalogRevision < 0 || receipt.CatalogRevision <= catalogBaseline &&
            entries.Any(entry => entry.Change is not LibraryBatchChange.Import))
            return ReceiptMismatch();

        var versions = new Dictionary<Guid, PlaylistVersion>();
        if (!receipt.Playlists.IsDefault)
        {
            foreach (var playlist in receipt.Playlists)
            {
                if (playlist?.Version is not { PlaylistId: var id, Revision: > 0 } || id == Guid.Empty ||
                    !versions.TryAdd(playlist.EntryOperationId, playlist.Version))
                    return ReceiptMismatch();
            }
        }
        if (versions.Count != playlistEntries.Length)
            return ReceiptMismatch();

        var queueSaves = new List<(Guid Operation, PublishedBatchQueueSave Publication)>();
        foreach (var entry in playlistEntries)
        {
            var staged = (LibraryBatchChange.Playlist)entry.Change;
            if (!versions.TryGetValue(entry.OperationId, out var version) ||
                staged.BasePlaylist is { } baseline && (version.PlaylistId != baseline.PlaylistId || version.Revision <= baseline.Revision))
                return ReceiptMismatch();
            if (staged.QueueSource is { } source)
            {
                if (staged.BasePlaylist is null)
                    return ReceiptMismatch();
                queueSaves.Add((entry.OperationId, new(source, version)));
            }
        }
        foreach (var saved in queueSaves)
            repository.RecordPublishedQueueSave(scope, saved.Operation, saved.Publication);
        var named = playlistEntries.Where(entry => entry.Change is LibraryBatchChange.Playlist { QueueSource: null })
            .GroupBy(entry => ((LibraryBatchChange.Playlist)entry.Change).DraftId).Select(group => group.Last());
        foreach (var entry in named)
            repository.BindPublishedDraft((LibraryBatchChange.Playlist)entry.Change, versions[entry.OperationId]);
        repository.RecordCommit(scope, new(seal, receipt));
        repository.RemoveEntries(scope, receipt.Version.BatchId);
        repository.Save(scope, EmptyBatch(current, seal.Command.KeepModeEnabled));
        return Success(receipt);

        static Outcome<LibraryBatchCommitReceipt> ReceiptMismatch() =>
            new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-receipt-mismatch", true);
    }

    private Outcome<bool> ReleaseRejectedSeal(ILibraryBatchRepository repository, LibraryBatchSeal seal)
    {
        var current = repository.Read(scope);
        if (current.Seal != seal)
            return Success(false);
        repository.Save(scope, current with { Seal = null });
        return Success(true);
    }

    private LibraryBatchReview ProjectReview(ILibraryBatchRepository repository, LibraryBatchRecord current, Guid batchId)
    {
        var entries = repository.ReadEntries(scope, batchId);
        if (entries.Length != current.ChangeCount || !entries.Select(entry => entry.Position).SequenceEqual(Enumerable.Range(1, current.ChangeCount)))
            throw new InvalidDataException("The local batch does not contain its full ordered review.");
        return new(new(batchId, current.Revision), current.ReviewId, entries);
    }

    private LibraryBatchMode ProjectMode(LibraryBatchRecord current) => new(scope.LibraryId, current.BatchId, current.Revision, current.ChangeCount, current.Seal is not null);

    private static LibraryBatchRecord EmptyBatch(LibraryBatchRecord current, bool enabled) =>
        new(enabled ? Guid.NewGuid() : null, checked(current.Revision + 1), 0);

    private bool Authorized(RequestContext request) => scope.LibraryId != Guid.Empty && scope.OriginInstanceId != Guid.Empty && request.OriginInstanceId == scope.OriginInstanceId;

    private bool ValidChange(LibraryBatchChange change) => change switch
    {
        LibraryBatchChange.TrackTags tags => tags.TrackId != Guid.Empty && tags.ExpectedRevision > 0 && tags.Before is not null && tags.Changes is { Count: > 0 },
        LibraryBatchChange.Import import => !import.Files.IsDefaultOrEmpty && import.Files.All(file => file.FileId != Guid.Empty && file.Size > 0 &&
            !string.IsNullOrWhiteSpace(file.Name) && file.Sha256 is { Length: 64 } && file.Sha256.All(Uri.IsHexDigit)),
        LibraryBatchChange.Organize organize => organize.Plan.LibraryId == scope.LibraryId && organize.Plan.PlanId != Guid.Empty && !organize.Plan.Moves.IsDefaultOrEmpty,
        LibraryBatchChange.Remove remove => remove.Plan.LibraryId == scope.LibraryId && remove.Plan.PlanId != Guid.Empty &&
            remove.Plan.PlanId == remove.Approval.PlanId && remove.Plan.Mode == remove.Approval.Mode && !remove.Plan.TrackIds.IsDefaultOrEmpty,
        LibraryBatchChange.Playlist playlist => !playlist.Entries.IsDefault && !string.IsNullOrWhiteSpace(playlist.Name) &&
            (playlist.QueueSource is not null ? playlist.BasePlaylist is not null : playlist.DraftId != Guid.Empty && playlist.DraftRevision > 0),
        _ => false
    };

    private static Task<Outcome<T>> Denied<T>() => Task.FromResult(Invalid<T>("library-batch-origin-unavailable"));
    private static Outcome<T> Success<T>(T value) => new Outcome<T>.Success(value);
    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);
    private static Outcome<T> Conflict<T>(string code, long revision) => new Outcome<T>.Conflict(code, revision);

    internal static Outcome<TResult> CopyFailure<TSource, TResult>(Outcome<TSource> failure) => failure switch
    {
        Outcome<TSource>.Invalid invalid => new Outcome<TResult>.Invalid(invalid.Issues),
        Outcome<TSource>.Conflict conflict => new Outcome<TResult>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<TSource>.DependencyFailure error => new Outcome<TResult>.DependencyFailure(error.Code, error.Retryable),
        Outcome<TSource>.Cancelled => new Outcome<TResult>.Cancelled(),
        _ => throw new InvalidOperationException("A failed batch preparation was expected.")
    };

    private sealed record CommitPreparation(LibraryBatchReview? Review, LibraryBatchCommitReceipt? Committed);
}
