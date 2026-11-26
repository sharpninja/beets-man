using BeetsMan.Desktop.Application.MediaSync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures the proposed profile and optional edits from a preview.</summary>
public sealed record LayoutPreviewInput(DeviceProfile Profile, LayoutEdits? Edits = null);

/// <summary>Owns preview, exact removal confirmation, and publication receipts.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-layouts and ac-safety while preserving the staged list across dialog cancellation.</remarks>
/// <remarks>2026-11-25: Bind Android peer selection and explicit staging profile to FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
public sealed partial class DeviceExportViewModel(PresentationServices services, Guid ownerId, SyncListViewModel syncList)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private DeviceLayoutsSnapshot? layouts;
    [ObservableProperty] private DeviceExportPreview? previewResult;
    [ObservableProperty] private OwnedRemovalProposal? removalProposal;
    [ObservableProperty] private ExportPublicationReceipt? publication;

    protected override Guid WriteOwner => syncList.Target.DeviceId;

    /// <summary>Clears previous device state, then loads the selected Android device's layout and staged list.</summary>
    /// <remarks>2026-11-26: Confirm the destination projection for FR-FILESYNC-001#ac-arrival.</remarks>
    internal Task<bool> SelectDeviceAsync(Guid? peerId, CancellationToken token)
    {
        var deviceId = peerId ?? Guid.Empty;
        if (deviceId != syncList.Target.DeviceId)
        {
            ChangeTarget(new(deviceId, string.Empty, 0));
            Layouts = null;
        }
        return deviceId != Guid.Empty && (Layouts is null || syncList.Plan is null)
            ? RefreshLayoutAsync(token)
            : Task.FromResult(true);
    }

    [RelayCommand(CanExecute = nameof(CanSelectLayout))]
    private Task SelectLayoutAsync(CancellationToken token) => RefreshLayoutAsync(token);

    /// <summary>Loads the layout and confirms any plan query required by its active staging profile.</summary>
    /// <remarks>2026-11-26: Keep incomplete loads retryable for FR-FILESYNC-001#ac-arrival.</remarks>
    private async Task<bool> RefreshLayoutAsync(CancellationToken token)
    {
        var deviceId = syncList.Target.DeviceId;
        if (deviceId == Guid.Empty)
            return false;
        var openPlan = Task.FromResult(true);
        var applied = await ReadAsync(new GetDeviceLayouts(deviceId), "layouts", result =>
        {
            Layouts = result;
            if (syncList.Target.ProfileRevision == 0 && result.ActiveProfile is { } current &&
                result.Profiles.Any(profile => profile.ProfileId == current.ProfileId && profile.Revision == current.Revision))
                syncList.SelectTarget(new(deviceId, current.ProfileId, current.Revision));
            if (syncList.Target.ProfileRevision > 0 && syncList.Plan is null)
                openPlan = syncList.LoadPlanAsync(null, token);
        }, token, result => result.DeviceId == deviceId && syncList.Target.DeviceId == deviceId).ConfigureAwait(false);
        return applied && await openPlan.ConfigureAwait(false);
    }

    /// <summary>Selects a displayed profile for staging and loads its plan without changing the active assignment.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectStagingProfile))]
    private Task SelectStagingProfileAsync(DeviceProfile? profile, CancellationToken token)
    {
        if (profile is null || !CanSelectStagingProfile(profile))
            return Task.CompletedTask;
        ChangeTarget(new(syncList.Target.DeviceId, profile.ProfileId, profile.Revision));
        return syncList.OpenAsync(null, token);
    }

    private bool CanSelectLayout() => IsActive && syncList.Target.DeviceId != Guid.Empty;
    private bool CanSelectStagingProfile(DeviceProfile? profile) => CanWrite && profile is not null &&
        Layouts is { } layouts && layouts.DeviceId == syncList.Target.DeviceId && layouts.Profiles.Contains(profile);

    private void ChangeTarget(DeviceExportTarget target)
    {
        if (target == syncList.Target)
            return;
        syncList.SelectTarget(target);
        Activate(Scope.OwnerId);
        CancelPreview();
        Publication = null;
    }

    [RelayCommand]
    private Task PreviewAsync(LayoutPreviewInput input, CancellationToken token) => PrepareAsync(input, token);

    [RelayCommand]
    private Task ResolveLayoutCollisionAsync(LayoutPreviewInput input, CancellationToken token) => PrepareAsync(input, token);

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private Task PublishAsync(CancellationToken token) => PublishPlanAsync(null, token);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private Task ConfirmOwnedRemovalAsync(bool acknowledged, CancellationToken token) =>
        CanConfirm(acknowledged) && RemovalProposal is { } proposal
            ? PublishPlanAsync(new OwnedRemovalApproval(proposal.ProposalId, proposal.PlanId, proposal.PlanRevision), token)
            : Task.CompletedTask;

    /// <summary>Discards the tentative review while leaving the current device layout assigned.</summary>
    /// <remarks>2026-11-24: Implement cancellation in STB20 for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelPreview()
    {
        RemovalProposal = null;
        PreviewResult = null;
        ClearFailure();
    }

    private bool CanCancel() => CanWrite && PreviewResult is not null && Failure?.CanRetry != true;

    protected override void OnWriteAvailabilityChanged()
    {
        PublishCommand.NotifyCanExecuteChanged(); ConfirmOwnedRemovalCommand.NotifyCanExecuteChanged(); CancelPreviewCommand.NotifyCanExecuteChanged();
        SelectLayoutCommand.NotifyCanExecuteChanged();
        SelectStagingProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnLayoutsChanged(DeviceLayoutsSnapshot? value) => SelectStagingProfileCommand.NotifyCanExecuteChanged();
    partial void OnPreviewResultChanged(DeviceExportPreview? value) => OnWriteAvailabilityChanged();
    partial void OnRemovalProposalChanged(OwnedRemovalProposal? value) => ConfirmOwnedRemovalCommand.NotifyCanExecuteChanged();

    private Task PrepareAsync(LayoutPreviewInput input, CancellationToken token)
    {
        var plan = syncList.Plan;
        var device = syncList.Target.DeviceId;
        return WriteAsync(new PrepareDeviceExportPreview(device, input.Profile.ProfileId, input.Profile.Revision,
            plan?.PlanId, plan?.PlanRevision, input.Edits), result =>
        {
            syncList.ApplyPreview(plan is null ? result.Plan : plan with { PlanRevision = result.Plan.PlanRevision });
            PreviewResult = result;
            RemovalProposal = null;
            Publication = null;
        }, token, result => result.Plan.DeviceId == device && syncList.Target.DeviceId == device &&
            result.Plan.ProfileId == input.Profile.ProfileId && result.Plan.ProfileRevision == input.Profile.Revision &&
            (plan?.PlanId is null || result.Plan.PlanId == plan.PlanId && result.Plan.PlanRevision >= plan.PlanRevision));
    }

    private bool CanPublish() => CanWrite && RemovalProposal is null && IsCurrentPreview();

    private bool CanConfirm(bool acknowledged) => CanWrite && acknowledged && IsCurrentPreview() && RemovalProposal is { } proposal &&
        PreviewResult?.Plan.PlanId == proposal.PlanId && PreviewResult?.Plan.PlanRevision == proposal.PlanRevision;

    private bool IsCurrentPreview() => PreviewResult is { BlockingIssues.IsDefaultOrEmpty: true } preview &&
        preview.Plan.PlanId is not null && preview.Plan.PlanRevision is not null &&
        syncList.Plan?.PlanId == preview.Plan.PlanId && syncList.Plan.PlanRevision == preview.Plan.PlanRevision;

    private Task PublishPlanAsync(OwnedRemovalApproval? approval, CancellationToken token) =>
        IsCurrentPreview() && PreviewResult?.Plan is { PlanId: { } id, PlanRevision: { } revision }
            ? WriteAsync(new PublishDeviceExport(id, revision, approval), result =>
            {
                switch (result)
                {
                    case DeviceExportSubmission.ConfirmationRequired needed:
                        RemovalProposal = needed.Proposal;
                        break;
                    case DeviceExportSubmission.Published published:
                        var plan = PreviewResult!.Plan;
                        syncList.ApplyPreview(plan);
                        if (Layouts is { } layouts && layouts.DeviceId == plan.DeviceId)
                            Layouts = layouts with { ActiveProfile = new(plan.ProfileId, plan.ProfileRevision) };
                        Publication = published.Receipt;
                        RemovalProposal = null;
                        PreviewResult = null;
                        break;
                }
            }, token, result => IsCurrentPreview() && result switch
            {
                DeviceExportSubmission.ConfirmationRequired needed => needed.Proposal.PlanId == id && needed.Proposal.PlanRevision == revision,
                DeviceExportSubmission.Published published => published.Receipt.PlanId == id && published.Receipt.PlanRevision >= revision,
                _ => false
            })
            : Task.CompletedTask;
}
