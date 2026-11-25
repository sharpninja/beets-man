using System.Collections.Immutable;
using BeetsMan.Desktop.Application.HostLibrary;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures the selected tracks and the displayed catalog version.</summary>
public sealed record CatalogTrackSelection(ImmutableArray<Guid> TrackIds, long CatalogRevision);

/// <summary>Captures a removal preview request and its explicit scope.</summary>
public sealed record RemovalSelection(CatalogTrackSelection Tracks, RemovalMode Mode);

/// <summary>Owns host setup, guided import, current jobs and versioned library previews.</summary>
/// <remarks>2026-11-24: Bind FR-BEETS-001#ac-open, ac-import, ac-edit and ac-recover independently from navigation.</remarks>
public sealed partial class HostLibraryViewModel(PresentationServices services, Guid ownerId, Guid libraryId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private HostLibraryState? library;
    [ObservableProperty] private HostLibraryConfiguration? configuration;
    [ObservableProperty] private ImportSourceSelection? sourceSelection;
    [ObservableProperty] private LibraryJobReceipt? acceptedJob;
    [ObservableProperty] private LibraryJobStatus? job;
    [ObservableProperty] private OrganizePlan? organizePlan;
    [ObservableProperty] private RemovalPlan? removalPlan;

    protected override Guid WriteOwner => libraryId;

    [RelayCommand]
    private Task OpenLibraryAsync(CancellationToken token) => ReadAsync(new GetHostLibraryState(libraryId), "library",
        result => Library = result, token, AcceptsLibrary);

    [RelayCommand]
    private Task InitializeLibraryAsync(HostLibrarySettings settings, CancellationToken token) => Library is { } current
        ? WriteAsync(new InitializeHostLibrary(libraryId, settings, current.Revision), result => Library = result, token, AcceptsLibrary)
        : Task.CompletedTask;

    [RelayCommand]
    private Task OpenConfigurationAsync(CancellationToken token) => ReadAsync(new GetHostLibraryConfiguration(libraryId), "configuration",
        result => Configuration = result, token, AcceptsConfiguration);

    [RelayCommand]
    private Task ApplyConfigurationAsync(HostLibrarySettings settings, CancellationToken token) => Configuration is { CanEdit: true } current
        ? WriteAsync(new SaveHostLibraryConfiguration(libraryId, settings, current.Revision), result => Configuration = result, token, AcceptsConfiguration)
        : Task.CompletedTask;

    [RelayCommand]
    private Task PickSourcesAsync(CancellationToken token) => WriteAsync(new SelectImportSources(Scope.OwnerId), result => SourceSelection = result, token);

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private Task UploadCopiesAsync(CancellationToken token) => CanUpload() && SourceSelection is { } selection
        ? WriteAsync(new UploadImportSources(libraryId, selection.SelectionToken), result =>
        {
            ApplyReceipt(result);
            if (SourceSelection == selection)
                SourceSelection = null;
        }, token, result => result.LibraryId == libraryId) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task ChooseImportMatchAsync(Guid matchId, CancellationToken token) => ReviewAsync(new ImportAction.UseMatch(matchId), token);

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task KeepTagsAsync(CancellationToken token) => ReviewAsync(new ImportAction.KeepTags(), token);

    [RelayCommand(CanExecute = nameof(CanReview))]
    private Task SkipImportItemAsync(CancellationToken token) => ReviewAsync(new ImportAction.Skip(), token);

    [RelayCommand]
    private Task InspectJobAsync(Guid jobId, CancellationToken token) => ReadAsync(new GetLibraryJob(jobId), "job", ApplyJob, token,
        result => result.JobId == jobId && AcceptsJob(result));

    [RelayCommand(CanExecute = nameof(CanRetryJob))]
    private Task RetryJobAsync(CancellationToken token) => Job is { Retryable: true } current
        ? WriteAsync(new RetryLibraryJob(current.JobId, current.Revision), ApplyReceipt, token, result => result.JobId == current.JobId)
        : Task.CompletedTask;

    [RelayCommand]
    private Task PreviewOrganizeAsync(CatalogTrackSelection selection, CancellationToken token) =>
        ReadAsync(new GetOrganizePlan(libraryId, selection.TrackIds, selection.CatalogRevision), "organize",
            result => OrganizePlan = result, token, result => result.LibraryId == libraryId && result.CatalogRevision == selection.CatalogRevision);

    [RelayCommand]
    private Task ConfirmMovesAsync(CancellationToken token) => CanEdit && OrganizePlan is { } plan
        ? WriteAsync(new OrganizeTracks(plan.PlanId, plan.CatalogRevision), result =>
        {
            ApplyReceipt(result);
            if (OrganizePlan == plan)
                OrganizePlan = null;
        }, token, result => result.LibraryId == libraryId) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviewRemovalAsync(RemovalSelection selection, CancellationToken token) =>
        ReadAsync(new GetRemovalPlan(libraryId, selection.Tracks.TrackIds, selection.Mode, selection.Tracks.CatalogRevision), "removal",
            result => RemovalPlan = result, token, result => result.LibraryId == libraryId && result.Mode == selection.Mode &&
                result.CatalogRevision == selection.Tracks.CatalogRevision);

    [RelayCommand]
    private Task ConfirmRemovalAsync(RemovalApproval approval, CancellationToken token) =>
        CanEdit && RemovalPlan is { } plan && plan.PlanId == approval.PlanId && plan.Mode == approval.Mode
            ? WriteAsync(new RemoveTracks(plan.PlanId, plan.CatalogRevision, approval), result =>
            {
                ApplyReceipt(result);
                if (RemovalPlan == plan)
                    RemovalPlan = null;
            }, token, result => result.LibraryId == libraryId) : Task.CompletedTask;

    /// <summary>Applies background progress for the selected job and ignores another job's events.</summary>
    public Task ReceiveJobAsync(LibraryJobStatus incoming) => PostAsync(Scope, () =>
    {
        if ((Job?.JobId ?? AcceptedJob?.JobId) == incoming.JobId && AcceptsJob(incoming))
            ApplyJob(incoming);
    });

    protected override void OnWriteAvailabilityChanged()
    {
        UploadCopiesCommand.NotifyCanExecuteChanged(); ChooseImportMatchCommand.NotifyCanExecuteChanged();
        KeepTagsCommand.NotifyCanExecuteChanged(); SkipImportItemCommand.NotifyCanExecuteChanged(); RetryJobCommand.NotifyCanExecuteChanged();
    }

    partial void OnLibraryChanged(HostLibraryState? value) => OnWriteAvailabilityChanged();
    partial void OnSourceSelectionChanged(ImportSourceSelection? value) => UploadCopiesCommand.NotifyCanExecuteChanged();
    partial void OnJobChanged(LibraryJobStatus? value) => OnWriteAvailabilityChanged();

    private bool CanEdit => Library is { CanEdit: true, IsOnline: true };
    private bool CanUpload() => CanWrite && CanEdit && SourceSelection is not null;
    private bool CanReview() => CanWrite && CanEdit && Job is { Phase: LibraryJobPhase.ReviewRequired, Review: not null };
    private bool CanRetryJob() => CanWrite && CanEdit && Job?.Retryable == true;

    private Task ReviewAsync(ImportAction choice, CancellationToken token) => CanReview() && Job is { Review: { } review } current
        ? WriteAsync(new ChooseImportAction(current.JobId, review.ItemId, choice, current.Revision), ApplyReceipt, token,
            result => result.JobId == current.JobId && result.LibraryId == libraryId) : Task.CompletedTask;

    private bool AcceptsLibrary(HostLibraryState incoming) => incoming.LibraryId == libraryId && incoming.Revision >= (Library?.Revision ?? 0);
    private bool AcceptsConfiguration(HostLibraryConfiguration incoming) => incoming.LibraryId == libraryId && incoming.Revision >= (Configuration?.Revision ?? 0);
    private bool AcceptsJob(LibraryJobStatus incoming) => incoming.LibraryId == libraryId &&
        (Job?.JobId != incoming.JobId || incoming.Revision >= Job.Revision) &&
        (AcceptedJob?.JobId != incoming.JobId || incoming.Revision >= AcceptedJob.Revision);

    private void ApplyJob(LibraryJobStatus incoming) => Job = incoming;
    private void ApplyReceipt(LibraryJobReceipt incoming)
    {
        AcceptedJob = incoming;
        if (Job?.JobId != incoming.JobId || Job.Revision < incoming.Revision)
            Job = new(incoming.JobId, incoming.LibraryId, incoming.Revision, LibraryJobPhase.Accepted);
    }
}

/// <summary>Owns an editable track tag draft and its accepted host job.</summary>
/// <remarks>2026-11-24: Bind revision-scoped tag edits and preserve the input under FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed partial class TagEditViewModel(PresentationServices services, Guid ownerId)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private TrackTagSnapshot? track;
    [ObservableProperty] private ImmutableDictionary<string, string?> changes = ImmutableDictionary<string, string?>.Empty;
    [ObservableProperty] private LibraryJobReceipt? acceptedJob;
    [ObservableProperty] private LibraryJobStatus? job;

    [RelayCommand]
    private Task OpenAsync(Guid trackId, CancellationToken token) => ReadAsync(new GetTrackTags(trackId), "tag-track", result =>
    {
        if (Track?.TrackId != result.TrackId)
        {
            Changes = ImmutableDictionary<string, string?>.Empty;
            AcceptedJob = null;
            Job = null;
        }
        Track = result;
    }, token, result => result.TrackId == trackId && (Track?.TrackId != trackId || result.CatalogRevision >= Track.CatalogRevision));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync(CancellationToken token) => Track is { } captured
        ? WriteAsync(new EditTrackTags(captured.TrackId, Changes, captured.CatalogRevision), result =>
        {
            AcceptedJob = result;
            Job = null;
        }, token, _ => Track?.TrackId == captured.TrackId) : Task.CompletedTask;

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
