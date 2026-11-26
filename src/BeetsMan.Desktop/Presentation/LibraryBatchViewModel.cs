using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies the visible import entry and its selected offered match.</summary>
public readonly record struct BatchImportMatchSelection(int Position, Guid MatchId);

/// <summary>Owns the batch toggle, exact review approval, and its guided import decisions.</summary>
/// <remarks>2026-11-25: Bind FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit, ac-batch-revert and ac-batch-toggle-off.</remarks>
public sealed partial class LibraryBatchViewModel(PresentationServices services, Guid ownerId, Guid libraryId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private LibraryBatchMode? mode;
    [ObservableProperty] private LibraryBatchReview? review;
    [ObservableProperty] private LibraryBatchImportReviews? importReviews;
    [ObservableProperty] private LibraryBatchCommitReceipt? lastCommit;

    private CommitLibraryBatch? attemptedCommit;
    private Guid attemptedOperation;
    private long minimumRevision;

    protected override Guid WriteOwner => libraryId;

    /// <summary>Refreshes the current toggle and any sealed approval from the desktop store.</summary>
    [RelayCommand]
    internal Task LoadAsync(CancellationToken token) => ReadModeAsync(token);

    /// <summary>Applies a newly accepted position in the current unsealed batch and invalidates any earlier review.</summary>
    /// <remarks>2026-11-25: Ignore replayed and closed-batch receipts for FR-LIBRARY-002#ac-batch-review and ac-batch-revert.</remarks>
    internal void ReceiveStage(LibraryBatchStageReceipt stage)
    {
        if (IsActive && Mode is { IsCommitting: false } current && current.BatchId == stage.Version.BatchId &&
            stage.Version.Revision > current.Revision && stage.Position > current.ChangeCount)
            ApplyMode(current with { Revision = stage.Version.Revision, ChangeCount = stage.Position });
    }

    [RelayCommand(CanExecute = nameof(CanSetMode))]
    private Task SetModeAsync(bool enabled, CancellationToken token) => CanSetMode(enabled) && Mode is { } current
        ? WriteAsync(new SetLibraryBatchMode(enabled, current.Revision), ApplyMode, token,
            incoming => AcceptsMode(incoming) && incoming.IsEnabled == enabled && !incoming.IsCommitting, blocksInteraction: true)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task OpenReviewAsync(CancellationToken token) => CanReview()
        ? ReadAsync(new GetLibraryBatchReview(), "batch-review", ApplyReview, token, AcceptsReview)
        : Task.CompletedTask;

    /// <summary>Approves only the displayed batch version and reuses the original operation when retried.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private Task CommitAsync(bool keepModeEnabled, CancellationToken token)
    {
        if (!CanCommit(keepModeEnabled) || Review is not { ReviewId: Guid reviewId } current)
            return Task.CompletedTask;
        var command = new CommitLibraryBatch(new(current.Version, reviewId), keepModeEnabled);
        if (attemptedCommit != command)
        {
            attemptedCommit = command;
            attemptedOperation = Guid.NewGuid();
        }
        var operation = attemptedOperation;
        return PublishAsync(command, current.Version, token, operation);
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeCommitAsync(CancellationToken token) => CanResume() && CurrentVersion is { } version
        ? PublishAsync(new ResumeLibraryBatchCommit(), version, token)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private Task RevertAsync(bool keepModeEnabled, CancellationToken token) => CanRevert(keepModeEnabled) && CurrentVersion is { } version
        ? WriteAsync(new RevertLibraryBatch(version, keepModeEnabled), ApplyMode, token,
            incoming => AcceptsMode(incoming) && incoming.Revision > version.Revision && incoming.BatchId != version.BatchId, blocksInteraction: true)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task OpenImportReviewsAsync(CancellationToken token) => CanResume()
        ? ReadAsync(new GetLibraryBatchImportReviews(), "batch-imports", incoming => ImportReviews = incoming, token, AcceptsImportReviews)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private Task KeepTagsAsync(int position, CancellationToken token) => ChooseAsync(position, new ImportAction.KeepTags(), token);

    [RelayCommand(CanExecute = nameof(CanUseMatch))]
    private Task UseMatchAsync(BatchImportMatchSelection selection, CancellationToken token) => CanUseMatch(selection)
        ? ChooseAsync(selection.Position, new ImportAction.UseMatch(selection.MatchId), token) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private Task SkipImportAsync(int position, CancellationToken token) => ChooseAsync(position, new ImportAction.Skip(), token);

    private LibraryBatchVersion? CurrentVersion => Mode is { BatchId: Guid batch } current ? new(batch, current.Revision) : null;

    private bool CanSetMode(bool enabled) => CanWrite && Mode is { IsCommitting: false } current && current.IsEnabled != enabled;
    private bool CanReview() => IsActive && Mode is { IsEnabled: true, ChangeCount: > 0 };
    private bool CanCommit(bool _) => CanWrite && Mode is { IsCommitting: false } && Review is { ReviewId: not null } current &&
        current.Version == CurrentVersion && !current.Entries.IsDefaultOrEmpty;
    private bool CanResume() => CanWrite && Mode is { IsEnabled: true, IsCommitting: true };
    private bool CanRevert(bool _) => CanWrite && CanReview();
    private bool CanChoose(int position) => CanResume() && ImportReviews?.Version == CurrentVersion && FindImport(position) is { Action: null };
    private bool CanUseMatch(BatchImportMatchSelection selected) => CanChoose(selected.Position) &&
        FindImport(selected.Position)!.Album.Matches.Any(match => match.MatchId == selected.MatchId && match.Details?.CanApply == true);

    private LibraryBatchImportReview? FindImport(int position) => ImportReviews?.Entries.FirstOrDefault(entry => entry.Position == position);

    private Task ReadModeAsync(CancellationToken token, bool preserveFailure = false) =>
        ReadAsync(new GetLibraryBatchMode(), "batch-mode", ApplyMode, token, AcceptsMode, preserveFailure);

    private bool AcceptsMode(LibraryBatchMode incoming) => incoming.LibraryId == libraryId && incoming.Revision >= minimumRevision &&
        incoming.Revision >= (Mode?.Revision ?? 0);

    private void ApplyMode(LibraryBatchMode incoming)
    {
        Mode = incoming;
        if (Review?.Version != CurrentVersion)
            Review = null;
        if (!incoming.IsCommitting || ImportReviews?.Version != CurrentVersion)
            ImportReviews = null;
        Services.PresentBatchMode(incoming);
    }

    private bool AcceptsReview(LibraryBatchReview incoming) => Mode is { BatchId: Guid batch } current &&
        incoming.Version.BatchId == batch && incoming.Version.Revision >= current.Revision && incoming.ReviewId is Guid reviewId && reviewId != Guid.Empty &&
        !incoming.Entries.IsDefaultOrEmpty && incoming.Entries.Select(entry => entry.Position).SequenceEqual(Enumerable.Range(1, incoming.Entries.Length));

    private void ApplyReview(LibraryBatchReview incoming)
    {
        ApplyMode(Mode! with { Revision = incoming.Version.Revision, ChangeCount = incoming.Entries.Length });
        Review = incoming;
    }

    /// <summary>Updates the mode after either a terminal result or an uncertain publication response.</summary>
    private Task PublishAsync(ICommand<LibraryBatchCommitReceipt> command, LibraryBatchVersion version, CancellationToken token, Guid? operation = null) =>
        WriteAsync(command, result =>
        {
            LastCommit = result;
            minimumRevision = checked(result.Version.Revision + 1);
            Review = null;
            ImportReviews = null;
            Mode = null;
        }, token, result => result.LibraryId == libraryId && result.Version == version && result.CatalogRevision >= 0 &&
            result.OperationId != Guid.Empty && (operation is null || result.OperationId == operation), blocksInteraction: true, operationId: operation,
            afterResult: () => ReadModeAsync(CancellationToken.None, preserveFailure: true));

    private bool AcceptsImportReviews(LibraryBatchImportReviews incoming) => Mode?.IsCommitting == true && incoming.Version == CurrentVersion &&
        incoming.ApprovalOperationId != Guid.Empty && !incoming.Entries.IsDefault &&
        (ImportReviews is not { } prior || incoming.ApprovalOperationId == prior.ApprovalOperationId && prior.Entries.All(existing =>
            incoming.Entries.Any(entry => entry.EntryOperationId == existing.EntryOperationId && entry.Revision >= existing.Revision)));

    private Task ChooseAsync(int position, ImportAction action, CancellationToken token)
    {
        if (!CanChoose(position) || ImportReviews is not { } current || FindImport(position) is not { } entry)
            return Task.CompletedTask;
        var operation = Guid.NewGuid();
        var request = new ChooseLibraryBatchImportAction(current.Version, position, entry.EntryOperationId, entry.Album.ItemId, entry.Revision, action);
        return WriteAsync(request, result =>
        {
            var latest = ImportReviews!;
            ImportReviews = latest with { Entries = [.. latest.Entries.Select(item => item.Position == position
                ? item with { Action = result.Action, Revision = result.Revision } : item)] };
        }, token, result => Mode?.IsCommitting == true && result.Version == CurrentVersion && result.Version == current.Version &&
            result.ApprovalOperationId == ImportReviews?.ApprovalOperationId && result.ApprovalOperationId == current.ApprovalOperationId &&
            result.Position == position && result.EntryOperationId == entry.EntryOperationId && result.OperationId == operation &&
            result.Action == action && result.Revision > entry.Revision && result.Revision >= FindImport(position)?.Revision,
            blocksInteraction: true, operationId: operation);
    }

    partial void OnModeChanged(LibraryBatchMode? value) => OnWriteAvailabilityChanged();
    partial void OnReviewChanged(LibraryBatchReview? value) => OnWriteAvailabilityChanged();
    partial void OnImportReviewsChanged(LibraryBatchImportReviews? value) => OnWriteAvailabilityChanged();

    protected override void OnWriteAvailabilityChanged()
    {
        SetModeCommand.NotifyCanExecuteChanged();
        OpenReviewCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        ResumeCommitCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        OpenImportReviewsCommand.NotifyCanExecuteChanged();
        KeepTagsCommand.NotifyCanExecuteChanged();
        UseMatchCommand.NotifyCanExecuteChanged();
        SkipImportCommand.NotifyCanExecuteChanged();
    }
}
