using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Presentation;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercises the Toolkit batch bindings through their typed dispatcher without creating a screen.</summary>
/// <remarks>2026-11-25: Bind TEST-LIBRARY-003 and TEST-DISPATCH-001 to review, sealed recovery, and guided choices.</remarks>
/// <remarks>2026-11-26: Trace dirty toggle handling to FR-LIBRARY-002#ac-batch-toggle-off.</remarks>
public sealed class LibraryBatchPresentationTests
{
    [Fact]
    public async Task ReviewsEveryEntryAndKeepsUncertainApprovalAvailableForRecovery()
    {
        var app = new BatchDispatcher();
        var services = new PresentationServices(app, new InlineScheduler(), app.Origin);
        using var viewModel = new LibraryBatchViewModel(services, Guid.NewGuid(), app.Library);
        using var hostViewModel = new HostLibraryViewModel(services, Guid.NewGuid(), app.Library);
        using var otherLibrary = new HostLibraryViewModel(services, Guid.NewGuid(), Guid.NewGuid());
        await viewModel.LoadCommand.ExecuteAsync(null);
        var initiallyDisabled = !viewModel.CommitCommand.CanExecute(true);
        Assert.False(viewModel.Mode!.IsEnabled);
        await viewModel.SetModeCommand.ExecuteAsync(true);
        Assert.Equal(viewModel.Mode, hostViewModel.BatchMode);
        Assert.Null(otherLibrary.BatchMode);

        app.StageEdits();
        await viewModel.LoadCommand.ExecuteAsync(null);
        await viewModel.SetModeCommand.ExecuteAsync(false);
        Assert.Equal("library-batch-resolution-required", viewModel.Failure?.Code);
        Assert.True(viewModel.Mode!.IsEnabled);

        app.ReviewOverride = app.Review with { Version = app.Review.Version with { Revision = app.Review.Version.Revision - 1 } };
        await viewModel.OpenReviewCommand.ExecuteAsync(null);
        var staleRejected = viewModel.Review is null && !viewModel.CommitCommand.CanExecute(true);
        app.ReviewOverride = null;
        await viewModel.OpenReviewCommand.ExecuteAsync(null);
        Assert.True(viewModel.CommitCommand.CanExecute(true));
        var order = string.Join(',', viewModel.Review!.Entries.Select(entry => entry.Change is LibraryBatchChange.TrackTags ? "Tags" : "Import"));

        var publish = viewModel.CommitCommand.ExecuteAsync(true);
        var blocked = viewModel.IsInteractionBlocked && viewModel.IsWriting && !viewModel.RevertCommand.CanExecute(true);
        app.ReturnPendingCommit();
        await publish;
        Assert.True(hostViewModel.BatchMode?.IsCommitting);
        var released = !viewModel.IsInteractionBlocked && !viewModel.IsWriting;
        var pendingFailure = viewModel.Failure is { Code: "library-batch-publication-pending", CanRetry: true };
        var canResume = viewModel.ResumeCommitCommand.CanExecute(null);
        var noNewApproval = !viewModel.CommitCommand.CanExecute(true);
        var canRevert = viewModel.RevertCommand.CanExecute(true);

        await viewModel.OpenImportReviewsCommand.ExecuteAsync(null);
        Assert.True(viewModel.UseMatchCommand.CanExecute(new BatchImportMatchSelection(2, app.ApplicableMatch)));
        Assert.False(viewModel.UseMatchCommand.CanExecute(new BatchImportMatchSelection(2, app.IncompleteMatch)));
        Assert.False(viewModel.KeepTagsCommand.CanExecute(1));
        await viewModel.SkipImportCommand.ExecuteAsync(2);
        var selected = Assert.IsType<ChooseLibraryBatchImportAction>(app.LastChoice);
        var noSecondDecision = !viewModel.SkipImportCommand.CanExecute(2) && !viewModel.KeepTagsCommand.CanExecute(2);
        await viewModel.OpenImportReviewsCommand.ExecuteAsync(null);
        Assert.IsType<ImportAction.Skip>(Assert.Single(viewModel.ImportReviews!.Entries).Action);

        await viewModel.RevertCommand.ExecuteAsync(true);
        var cleared = viewModel.Mode is { IsEnabled: true, ChangeCount: 0, IsCommitting: false } &&
            viewModel.Review is null && viewModel.ImportReviews is null && viewModel.LastCommit is null;
        Assert.Equal(viewModel.Mode, hostViewModel.BatchMode);
        LibraryBatchPresentationAssertions.Verify(new(initiallyDisabled, order, blocked, released, pendingFailure, canResume,
            selected.Action is ImportAction.Skip ? "Skip" : selected.Action.GetType().Name, selected.Position, selected.ExpectedRevision,
            noSecondDecision, noNewApproval, canRevert, cleared, staleRejected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopensSealedApprovalAndDispatchesExactImportChoiceBeforeResuming(bool useMatch)
    {
        var app = new BatchDispatcher();
        app.StageEdits();
        app.ReturnPendingCommit();
        using var viewModel = new LibraryBatchViewModel(new(app, new InlineScheduler(), app.Origin), Guid.NewGuid(), app.Library);
        await viewModel.LoadCommand.ExecuteAsync(null);
        Assert.True(viewModel.ResumeCommitCommand.CanExecute(null));
        Assert.False(viewModel.CommitCommand.CanExecute(true));
        await viewModel.OpenImportReviewsCommand.ExecuteAsync(null);
        if (useMatch)
        {
            await viewModel.UseMatchCommand.ExecuteAsync(new BatchImportMatchSelection(2, app.ApplicableMatch));
            Assert.Equal(new ImportAction.UseMatch(app.ApplicableMatch), app.LastChoice!.Action);
        }
        else
        {
            await viewModel.KeepTagsCommand.ExecuteAsync(2);
            Assert.IsType<ImportAction.KeepTags>(app.LastChoice!.Action);
        }
        await viewModel.ResumeCommitCommand.ExecuteAsync(null);
        Assert.Equal(app.Approval, viewModel.LastCommit!.OperationId);
        Assert.False(viewModel.Mode!.IsEnabled);
        Assert.Null(viewModel.ImportReviews);
        Assert.False(viewModel.ResumeCommitCommand.CanExecute(null));
        Assert.False(viewModel.IsInteractionBlocked);
    }

    [Fact]
    public async Task RetryingFromActionFeedbackRefreshesTheModeWithTheOriginalOperation()
    {
        var app = new BatchDispatcher();
        app.StageEdits();
        var services = new PresentationServices(app, new InlineScheduler(), app.Origin);
        using var viewModel = new LibraryBatchViewModel(services, Guid.NewGuid(), app.Library);
        using var shell = new ShellViewModel(services, Guid.NewGuid());
        using var error = new ActionErrorViewModel(services, shell);
        await viewModel.LoadCommand.ExecuteAsync(null);
        await viewModel.OpenReviewCommand.ExecuteAsync(null);
        var attempt = viewModel.CommitCommand.ExecuteAsync(true);
        app.ReturnPendingCommit();
        await attempt;
        error.Show(Guid.NewGuid(), viewModel.Failure!);
        await error.RetryCommand.ExecuteAsync(null);
        Assert.Equal(app.Approval, viewModel.LastCommit!.OperationId);
        Assert.False(viewModel.Mode!.IsEnabled);
        Assert.Null(viewModel.Failure);
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            apply();
            return Task.CompletedTask;
        }
    }

    private sealed class BatchDispatcher : IRequestDispatcher
    {
        private readonly TaskCompletionSource<Outcome<LibraryBatchCommitReceipt>> commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Guid batchId = Guid.NewGuid();
        private readonly Guid entryId = Guid.NewGuid();
        private readonly Guid importId = Guid.NewGuid();
        private LibraryBatchMode mode;

        internal BatchDispatcher() => mode = new(Library, null, 0, 0, false);

        internal Guid Library { get; } = Guid.NewGuid();
        internal Guid Origin { get; } = Guid.NewGuid();
        internal Guid Approval { get; private set; } = Guid.NewGuid();
        internal Guid ApplicableMatch { get; } = Guid.NewGuid();
        internal Guid IncompleteMatch { get; } = Guid.NewGuid();
        internal LibraryBatchReview Review { get; private set; } = null!;
        internal LibraryBatchReview? ReviewOverride { get; set; }
        internal ChooseLibraryBatchImportAction? LastChoice { get; private set; }

        internal void StageEdits()
        {
            mode = mode with { BatchId = batchId, Revision = 3, ChangeCount = 2 };
            Review = new(new(batchId, 3), Guid.NewGuid(),
                [new(Guid.NewGuid(), 1, new LibraryBatchChange.TrackTags(Guid.NewGuid(), 1, ImmutableSortedDictionary<string, string>.Empty,
                    ImmutableSortedDictionary<string, string?>.Empty.Add("title", "New title"))),
                new(entryId, 2, new LibraryBatchChange.Import([new(Guid.NewGuid(), "Album.flac", 4, new string('A', 64))]))]);
        }

        internal void ReturnPendingCommit()
        {
            mode = mode with { IsCommitting = true };
            commit.SetResult(new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-publication-pending", true));
        }

        public Task<Outcome<T>> QueryAsync<T>(IQuery<T> request, RequestContext context, CancellationToken cancellationToken)
        {
            Assert.Equal(Origin, context.OriginInstanceId);
            object result = request switch
            {
                GetLibraryBatchMode => new Outcome<LibraryBatchMode>.Success(mode),
                GetLibraryBatchReview => new Outcome<LibraryBatchReview>.Success(ReviewOverride ?? Review),
                GetLibraryBatchImportReviews => new Outcome<LibraryBatchImportReviews>.Success(new(Review.Version, Approval,
                    [new(2, entryId, 1, new(importId, [new(ApplicableMatch, "Album", "Artist", new(null, null, 1, true, [])),
                        new(IncompleteMatch, "Incomplete", "Artist", new(null, null, 2, false, []))]), null)])),
                _ => throw new InvalidOperationException($"Unexpected query {request}.")
            };
            return Task.FromResult((Outcome<T>)result);
        }

        public async Task<Outcome<T>> SendAsync<T>(ICommand<T> request, CommandContext context, CancellationToken cancellationToken)
        {
            Assert.Equal(Origin, context.Request.OriginInstanceId);
            Assert.NotEqual(Guid.Empty, context.OperationId);
            object result = request switch
            {
                SetLibraryBatchMode set => SetMode(set),
                CommitLibraryBatch submit => await CommitAsync(submit, context.OperationId, cancellationToken),
                RevertLibraryBatch revert => Revert(revert),
                ChooseLibraryBatchImportAction choice => Choose(choice, context.OperationId),
                ResumeLibraryBatchCommit => Resume(),
                _ => throw new InvalidOperationException($"Unexpected command {request}.")
            };
            return (Outcome<T>)result;
        }

        private Outcome<LibraryBatchMode> SetMode(SetLibraryBatchMode request)
        {
            Assert.Equal(mode.Revision, request.ExpectedRevision);
            if (!request.Enabled)
                return new Outcome<LibraryBatchMode>.Conflict("library-batch-resolution-required", mode.Revision);
            mode = mode with { BatchId = batchId, Revision = 1 };
            return new Outcome<LibraryBatchMode>.Success(mode);
        }

        private Task<Outcome<LibraryBatchCommitReceipt>> CommitAsync(CommitLibraryBatch request, Guid operationId, CancellationToken token)
        {
            Assert.Equal(new LibraryBatchApproval(Review.Version, Review.ReviewId!.Value), request.Approval);
            Assert.True(request.KeepModeEnabled);
            if (mode.IsCommitting)
            {
                Assert.Equal(Approval, operationId);
                return Task.FromResult(Resume());
            }
            Approval = operationId;
            return commit.Task.WaitAsync(token);
        }

        private Outcome<LibraryBatchImportChoiceReceipt> Choose(ChooseLibraryBatchImportAction request, Guid operationId)
        {
            Assert.Equal(Review.Version, request.Version);
            Assert.Equal(entryId, request.EntryOperationId);
            Assert.Equal(importId, request.ReviewItemId);
            LastChoice = request;
            return new Outcome<LibraryBatchImportChoiceReceipt>.Success(new(request.Version, Approval, request.Position, entryId,
                operationId, request.ExpectedRevision + 1, request.Action));
        }

        private Outcome<LibraryBatchMode> Revert(RevertLibraryBatch request)
        {
            Assert.Equal(Review.Version, request.Version);
            Assert.True(mode.IsCommitting);
            mode = new(Library, request.KeepModeEnabled ? Guid.NewGuid() : null, 4, 0, false);
            return new Outcome<LibraryBatchMode>.Success(mode);
        }

        private Outcome<LibraryBatchCommitReceipt> Resume()
        {
            Assert.True(mode.IsCommitting);
            mode = new(Library, null, 4, 0, false);
            return new Outcome<LibraryBatchCommitReceipt>.Success(new(Library, Review.Version, Approval, 1, []));
        }
    }
}
