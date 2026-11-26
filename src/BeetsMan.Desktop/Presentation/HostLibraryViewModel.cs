using System.Collections.Immutable;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures the selected tracks and the displayed catalog version.</summary>
public sealed record CatalogTrackSelection(ImmutableArray<Guid> TrackIds, long CatalogRevision);

/// <summary>Captures a removal preview request and its explicit scope.</summary>
public sealed record RemovalSelection(CatalogTrackSelection Tracks, RemovalMode Mode);

/// <summary>Owns host setup, guided import, current jobs and versioned library previews.</summary>
/// <remarks>2026-11-24: Allow private import staging while disconnected for FR-LIBRARY-002#ac-batch-local.</remarks>
/// <remarks>2026-11-24: Bind FR-BEETS-001#ac-open, ac-import, ac-edit and ac-recover independently from navigation.</remarks>
/// <remarks>2026-11-24: Reuse maintenance approval operations and allow local confirmation after disconnect for FR-BEETS-001#ac-edit, ac-recover and FR-LIBRARY-002#ac-batch-local.</remarks>
/// <remarks>2026-11-25: Expose the blocking initialization state and approved revision for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Share the host settings query and edit authority with the SCR29 entry for FR-BEETS-001#ac-open.</remarks>
public sealed partial class HostLibraryViewModel : PresentationViewModel
{
    [ObservableProperty] private HostLibraryState? library;
    [ObservableProperty] private HostLibraryConfiguration? configuration;
    [ObservableProperty] private ImportSourceSelection? sourceSelection;
    [ObservableProperty] private LibraryJobReceipt? acceptedJob;
    [ObservableProperty] private LibraryBatchMode? batchMode;
    [ObservableProperty] private LibraryBatchStageReceipt? stagedChange;
    [ObservableProperty] private LibraryJobStatus? job;
    [ObservableProperty] private OrganizePlan? organizePlan;
    [ObservableProperty] private RemovalPlan? removalPlan;

    private Guid uploadSelectionToken;
    private Guid uploadOperationId;
    private ChooseImportAction? attemptedReview;
    private Guid reviewOperationId;
    private RetryLibraryJob? attemptedRetry;
    private Guid retryOperationId;
    private Guid organizeOperationId;
    private Guid removalOperationId;
    private readonly Guid libraryId;

    /// <summary>Listens for the owning library's batch-mode projection alongside host results.</summary>
    /// <remarks>2026-11-25: Refresh offline command availability for FR-LIBRARY-002#ac-batch-local.</remarks>
    public HostLibraryViewModel(PresentationServices services, Guid ownerId, Guid libraryId) : base(services, ownerId)
    {
        this.libraryId = libraryId;
        services.BatchModeChanged += ApplyBatchMode;
    }

    protected override Guid WriteOwner => libraryId;

    [RelayCommand]
    private Task OpenLibraryAsync(CancellationToken token) => ReadAsync(new GetHostLibraryState(libraryId), "library",
        result => Library = result, token, AcceptsLibrary);

    [RelayCommand]
    private Task InitializeLibraryAsync(HostLibrarySettings settings, CancellationToken token) => Library is { IsOnline: true, CanEdit: true, IsInitialized: false } current
        ? WriteAsync(new InitializeHostLibrary(libraryId, settings, current.Revision), result => Library = result, token, AcceptsLibrary,
            blocksInteraction: true)
        : Task.CompletedTask;

    [RelayCommand]
    private Task OpenConfigurationAsync(CancellationToken token) => ReadConfigurationAsync(token);

    /// <summary>Loads the single host library configuration projection used by both entry points.</summary>
    internal Task ReadConfigurationAsync(CancellationToken token) => ReadAsync(new GetHostLibraryConfiguration(libraryId), "configuration",
        result => Configuration = result, token, AcceptsConfiguration);

    [RelayCommand(CanExecute = nameof(CanApplyConfiguration))]
    private Task ApplyConfigurationAsync(HostLibrarySettings settings, CancellationToken token) => CanApplyConfiguration() && Configuration is { } current
        ? WriteAsync(new SaveHostLibraryConfiguration(libraryId, settings, current.Revision), result => Configuration = result, token,
            AcceptsConfiguration, blocksInteraction: true)
        : Task.CompletedTask;

    [RelayCommand]
    private Task PickSourcesAsync(CancellationToken token) => WriteAsync(new SelectImportSources(Scope.OwnerId), result => SourceSelection = result, token);

    /// <summary>Reuses the upload operation when the selected sources have an uncertain host result.</summary>
    /// <remarks>2026-11-24: Recover the same guided-import receipt for FR-BEETS-001#ac-import and ac-recover.</remarks>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private Task UploadCopiesAsync(CancellationToken token)
    {
        if (!CanUpload() || SourceSelection is not { } selection)
            return Task.CompletedTask;
        if (uploadSelectionToken != selection.SelectionToken || uploadOperationId == Guid.Empty)
        {
            uploadSelectionToken = selection.SelectionToken;
            uploadOperationId = Guid.NewGuid();
        }
        return WriteAsync(new UploadImportSources(libraryId, selection.SelectionToken), result =>
        {
            ApplyReceipt(result);
            if (SourceSelection == selection)
                SourceSelection = null;
        }, token, result => result.LibraryId == libraryId, operationId: uploadOperationId);
    }

    [RelayCommand(CanExecute = nameof(CanChooseImportMatch))]
    private Task ChooseImportMatchAsync(Guid matchId, CancellationToken token) => CanChooseImportMatch(matchId)
        ? ReviewAsync(new ImportAction.UseMatch(matchId), token) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task KeepTagsAsync(CancellationToken token) => ReviewAsync(new ImportAction.KeepTags(), token);

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task SkipImportItemAsync(CancellationToken token) => ReviewAsync(new ImportAction.Skip(), token);

    [RelayCommand]
    private Task InspectJobAsync(Guid jobId, CancellationToken token) => ReadAsync(new GetLibraryJob(jobId), "job", ApplyJob, token,
        result => result.JobId == jobId && AcceptsJob(result));

    /// <summary>Resubmits an uncertain retry using the same job revision and operation ID.</summary>
    /// <remarks>2026-11-24: Recover the durable guided retry receipt for FR-BEETS-001#ac-recover.</remarks>
    [RelayCommand(CanExecute = nameof(CanRetryJob))]
    private Task RetryJobAsync(CancellationToken token)
    {
        if (!CanRetryJob() || Job is not { } current)
            return Task.CompletedTask;
        var request = new RetryLibraryJob(current.JobId, current.Revision);
        if (attemptedRetry != request)
        {
            attemptedRetry = request;
            retryOperationId = Guid.NewGuid();
        }
        return WriteAsync(request, ApplyReceipt, token,
            result => result.JobId == current.JobId && result.LibraryId == libraryId && Job?.JobId == current.JobId,
            operationId: retryOperationId);
    }

    /// <summary>Replaces the prior organize preview with the result for this selection.</summary>
    [RelayCommand]
    private Task PreviewOrganizeAsync(CatalogTrackSelection selection, CancellationToken token)
    {
        OrganizePlan = null;
        ClearFailure();
        return ReadAsync(new GetOrganizePlan(libraryId, selection.TrackIds, selection.CatalogRevision), "organize",
            result => OrganizePlan = result, token, result => result.LibraryId == libraryId && result.CatalogRevision == selection.CatalogRevision);
    }

    /// <summary>Confirms the displayed moves with the same identity after an uncertain reply.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmMoves))]
    private Task ConfirmMovesAsync(CancellationToken token) => CanConfirmMoves() && OrganizePlan is { } plan
        ? WriteAsync(new OrganizeTracks(plan.PlanId, plan.CatalogRevision, plan), result =>
        {
            ApplyReceipt(result);
            if (OrganizePlan == plan)
                OrganizePlan = null;
        }, token, result => result.LibraryId == libraryId, operationId: organizeOperationId) : Task.CompletedTask;

    /// <summary>Replaces the prior removal preview when the selection or requested removal mode changes.</summary>
    [RelayCommand]
    private Task PreviewRemovalAsync(RemovalSelection selection, CancellationToken token)
    {
        RemovalPlan = null;
        ClearFailure();
        return ReadAsync(new GetRemovalPlan(libraryId, selection.Tracks.TrackIds, selection.Mode, selection.Tracks.CatalogRevision), "removal",
            result => RemovalPlan = result, token, result => result.LibraryId == libraryId && result.Mode == selection.Mode &&
                result.CatalogRevision == selection.Tracks.CatalogRevision);
    }

    /// <summary>Confirms removal only for the displayed plan and the explicitly chosen mode.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRemoval))]
    private Task ConfirmRemovalAsync(RemovalApproval? approval, CancellationToken token) =>
        CanConfirmRemoval(approval) && RemovalPlan is { } plan && approval is not null
            ? WriteAsync(new RemoveTracks(plan.PlanId, plan.CatalogRevision, approval, plan), result =>
            {
                ApplyReceipt(result);
                if (RemovalPlan == plan)
                    RemovalPlan = null;
            }, token, result => result.LibraryId == libraryId, operationId: removalOperationId) : Task.CompletedTask;

    /// <summary>Applies background progress for the selected job and ignores another job's events.</summary>
    public Task ReceiveJobAsync(LibraryJobStatus incoming) => PostAsync(Scope, () =>
    {
        if ((Job?.JobId ?? AcceptedJob?.JobId) == incoming.JobId && AcceptsJob(incoming))
            ApplyJob(incoming);
    });

    protected override void OnWriteAvailabilityChanged()
    {
        ApplyConfigurationCommand.NotifyCanExecuteChanged();
        UploadCopiesCommand.NotifyCanExecuteChanged(); ChooseImportMatchCommand.NotifyCanExecuteChanged();
        KeepTagsCommand.NotifyCanExecuteChanged(); SkipImportItemCommand.NotifyCanExecuteChanged(); RetryJobCommand.NotifyCanExecuteChanged();
        ConfirmMovesCommand.NotifyCanExecuteChanged(); ConfirmRemovalCommand.NotifyCanExecuteChanged();
    }

    partial void OnLibraryChanged(HostLibraryState? value) => OnWriteAvailabilityChanged();
    partial void OnConfigurationChanged(HostLibraryConfiguration? value) => ApplyConfigurationCommand.NotifyCanExecuteChanged();
    partial void OnSourceSelectionChanged(ImportSourceSelection? value) => UploadCopiesCommand.NotifyCanExecuteChanged();
    partial void OnBatchModeChanged(LibraryBatchMode? value) => OnWriteAvailabilityChanged();
    partial void OnJobChanged(LibraryJobStatus? value) => OnWriteAvailabilityChanged();

    partial void OnOrganizePlanChanged(OrganizePlan? value)
    {
        organizeOperationId = value is null ? Guid.Empty : Guid.NewGuid();
        ConfirmMovesCommand.NotifyCanExecuteChanged();
    }

    partial void OnRemovalPlanChanged(RemovalPlan? value)
    {
        removalOperationId = value is null ? Guid.Empty : Guid.NewGuid();
        ConfirmRemovalCommand.NotifyCanExecuteChanged();
    }

    private bool CanEdit => Library is { CanEdit: true, IsOnline: true };
    private bool CanApplyConfiguration() => CanWrite && Configuration is { CanEdit: true };
    private bool CanUpload() => CanWrite && SourceSelection is not null && (BatchMode is { IsEnabled: true }
        ? !BatchMode.IsCommitting : CanEdit);
    private bool CanConfirmMaintenance => CanWrite && (BatchMode is { IsEnabled: true } batch ? !batch.IsCommitting : CanEdit);
    private bool CanConfirmMoves() => CanConfirmMaintenance && OrganizePlan is { } plan && !plan.Moves.IsDefaultOrEmpty;
    private bool CanConfirmRemoval(RemovalApproval? approval) => CanConfirmMaintenance && approval is not null &&
        RemovalPlan is { } plan && plan.PlanId == approval.PlanId && plan.Mode == approval.Mode;

    /// <summary>Applies the local batch setting whenever its scope receives an updated mode.</summary>
    public Task ReceiveBatchModeAsync(LibraryBatchMode incoming) => PostAsync(Scope, () => ApplyBatchMode(incoming));

    private void ApplyBatchMode(LibraryBatchMode incoming)
    {
        if (!IsActive || incoming.LibraryId != libraryId || incoming.Revision < (BatchMode?.Revision ?? 0))
            return;
        BatchMode = incoming;
        if (StagedChange is { } staged && staged.Version.BatchId != incoming.BatchId)
            StagedChange = null;
    }

    protected override void OnDisposed() => Services.BatchModeChanged -= ApplyBatchMode;
    private bool CanReview() => CanWrite && CanEdit && Job is { Phase: LibraryJobPhase.ReviewRequired, Review: not null };
    private bool CanChooseImportMatch(Guid matchId) => CanReview() && Job?.Review?.Matches.Any(match =>
        match.MatchId == matchId && match.Details?.CanApply != false) == true;
    private bool CanRetryJob() => CanWrite && CanEdit && Job?.Retryable == true;

    /// <summary>Submits a review choice and reuses its operation ID if the reply is lost.</summary>
    /// <remarks>2026-11-24: Recover the original approval and project its job phase for FR-BEETS-001#ac-import and ac-recover.</remarks>
    private Task ReviewAsync(ImportAction choice, CancellationToken token)
    {
        if (!CanReview() || Job is not { Review: { } review } current)
            return Task.CompletedTask;
        var request = new ChooseImportAction(current.JobId, review.ItemId, choice, current.Revision);
        if (attemptedReview != request)
        {
            attemptedReview = request;
            reviewOperationId = Guid.NewGuid();
        }
        var phase = choice is ImportAction.Skip ? LibraryJobPhase.Skipped : LibraryJobPhase.Running;
        return WriteAsync(request, result =>
        {
            AcceptedJob = result;
            if (Job?.JobId == result.JobId && Job.Revision < result.Revision)
                Job = new(result.JobId, result.LibraryId, result.Revision, phase);
        }, token, result => result.JobId == current.JobId && result.LibraryId == libraryId && Job?.JobId == current.JobId,
            operationId: reviewOperationId);
    }

    private bool AcceptsLibrary(HostLibraryState incoming) => incoming.LibraryId == libraryId && incoming.Revision >= (Library?.Revision ?? 0);
    private bool AcceptsConfiguration(HostLibraryConfiguration incoming) => incoming.LibraryId == libraryId && incoming.Revision >= (Configuration?.Revision ?? 0);
    private bool AcceptsJob(LibraryJobStatus incoming) => incoming.LibraryId == libraryId &&
        (Job?.JobId != incoming.JobId || incoming.Revision >= Job.Revision) &&
        (AcceptedJob?.JobId != incoming.JobId || incoming.Revision >= AcceptedJob.Revision);

    private void ApplyJob(LibraryJobStatus incoming) => Job = incoming;
    /// <summary>Projects the local stage or the accepted host job as reported by the handler.</summary>
    /// <remarks>2026-11-25: Share locally accepted imports and maintenance with FR-LIBRARY-002#ac-batch-review.</remarks>
    private void ApplyReceipt(LibraryJobReceipt incoming)
    {
        StagedChange = incoming.LocalBatch;
        if (incoming.LocalBatch is { } stage)
        {
            AcceptedJob = null;
            Job = null;
            Services.PresentBatchStage(stage);
            return;
        }
        AcceptedJob = incoming;
        if (Job?.JobId != incoming.JobId || Job.Revision < incoming.Revision)
            Job = new(incoming.JobId, incoming.LibraryId, incoming.Revision, LibraryJobPhase.Accepted);
    }
}

/// <summary>Owns an editable track tag draft and its accepted host job.</summary>
/// <remarks>2026-11-24: Bind revision-scoped tag edits and preserve the input under FR-BEETS-001#ac-edit and ac-recover.</remarks>
/// <remarks>2026-11-24: Project a local tag stage separately from a host job for FR-LIBRARY-002#ac-batch-local.</remarks>
public sealed partial class TagEditViewModel(PresentationServices services, Guid ownerId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private TrackTagSnapshot? track;
    [ObservableProperty] private ImmutableDictionary<string, string?> changes = ImmutableDictionary<string, string?>.Empty;
    [ObservableProperty] private LibraryJobReceipt? acceptedJob;
    [ObservableProperty] private LibraryBatchStageReceipt? stagedEdit;
    [ObservableProperty] private LibraryJobStatus? job;
    private EditTrackTags? attemptedEdit;
    private Guid tagOperationId;

    [RelayCommand]
    private Task OpenAsync(Guid trackId, CancellationToken token) => ReadAsync(new GetTrackTags(trackId), "tag-track", result =>
    {
        if (Track?.TrackId != result.TrackId || (StagedEdit is { } oldStage && oldStage.Version.BatchId != result.OverlayBatchId))
        {
            Changes = ImmutableDictionary<string, string?>.Empty;
            AcceptedJob = null;
            StagedEdit = null;
            Job = null;
            attemptedEdit = null;
        }
        Track = result;
    }, token, result => result.TrackId == trackId && (Track?.TrackId != trackId || result.CatalogRevision >= Track.CatalogRevision));

    /// <summary>Admits a new draft or reuses the same operation after an uncertain response.</summary>
    /// <remarks>2026-11-24: Retain the exact draft identity for FR-BEETS-001#ac-edit and ac-recover.</remarks>
    /// <remarks>2026-11-25: Share a local tag stage with FR-LIBRARY-002#ac-batch-review.</remarks>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync(CancellationToken token)
    {
        if (!CanSave() || Track is not { } captured)
            return Task.CompletedTask;
        var request = new EditTrackTags(captured.TrackId, Changes, captured.CatalogRevision);
        if (attemptedEdit is not { } previous || previous.TrackId != request.TrackId ||
            previous.ExpectedCatalogRevision != request.ExpectedCatalogRevision || previous.TagChanges.Count != request.TagChanges.Count ||
            previous.TagChanges.Any(tag => !request.TagChanges.TryGetValue(tag.Key, out var value) || tag.Value != value))
        {
            attemptedEdit = request;
            tagOperationId = Guid.NewGuid();
        }
        return WriteAsync(request, result =>
        {
            StagedEdit = result.LocalBatch;
            AcceptedJob = result.LocalBatch is null ? result : null;
            Job = null;
            if (result.LocalBatch is { } stage)
                Services.PresentBatchStage(stage);
        }, token, _ => Track?.TrackId == captured.TrackId, operationId: tagOperationId);
    }

    /// <summary>Applies the matching tag job's progress and exposes its publication revision.</summary>
    public Task ReceiveJobAsync(LibraryJobStatus incoming) => PostAsync(Scope, () =>
    {
        if (AcceptedJob is { } accepted && accepted.JobId == incoming.JobId && accepted.LibraryId == incoming.LibraryId &&
            incoming.Revision >= accepted.Revision && incoming.Revision >= (Job?.Revision ?? 0))
            Job = incoming;
    });

    protected override void OnWriteAvailabilityChanged() => SaveCommand.NotifyCanExecuteChanged();
    partial void OnTrackChanged(TrackTagSnapshot? value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnChangesChanged(ImmutableDictionary<string, string?> value) => SaveCommand.NotifyCanExecuteChanged();
    private bool CanSave() => CanWrite && Track is not null && Changes.Count > 0;
}
