using System.Collections.Immutable;
using BeetsMan.Desktop.Application.MediaSync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies the planned device and exact version of the selected player profile.</summary>
public sealed record DeviceExportTarget(Guid DeviceId, string ProfileId, long ProfileRevision);

/// <summary>Owns the staged list independently of preview and transfer.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-playlist-drop to one typed staging request with the selected app profile and plan revision.</remarks>
public sealed partial class SyncListViewModel(PresentationServices services, Guid ownerId, DeviceExportTarget target)
    : PresentationViewModel(services, ownerId)
{
    private DeviceExportTarget currentTarget = target;
    private DeviceExportPlan? plan;

    [ObservableProperty] private ImmutableArray<Guid> selectedEntryIds = [];

    /// <summary>Gets the current device and player profile.</summary>
    public DeviceExportTarget Target => currentTarget;

    /// <summary>Gets the scope's staged playlist and audio entries.</summary>
    public DeviceExportPlan? Plan => plan;

    protected override Guid WriteOwner => Target.DeviceId;

    /// <summary>Replaces the staged-list scope after an explicit target change.</summary>
    public void SelectTarget(DeviceExportTarget next)
    {
        if (Target == next)
            return;
        currentTarget = next;
        Activate(Scope.OwnerId);
        SetProperty(ref plan, null, nameof(Plan));
        SelectedEntryIds = [];
        OnPropertyChanged(nameof(Target));
    }

    [RelayCommand]
    private Task OpenAsync(Guid? planId, CancellationToken token) => ReadAsync(new GetDeviceExportPlan(Target.DeviceId, planId, Target.ProfileId,
        Target.ProfileRevision), "plan",
        Apply, token, incoming => Accepts(incoming) && (planId is null || incoming.PlanId == planId));

    [RelayCommand(CanExecute = nameof(CanAddTracks))]
    private Task AddSelectedAsync(ImmutableArray<Guid> tracks, CancellationToken token) => StageTracksAsync(tracks, token);

    [RelayCommand(CanExecute = nameof(CanAddTracks))]
    private Task DropTracksAsync(ImmutableArray<Guid> tracks, CancellationToken token) => StageTracksAsync(tracks, token);

    [RelayCommand(CanExecute = nameof(CanDropPlaylist))]
    private Task DropSavedPlaylistAsync(Guid playlistId, CancellationToken token) => CanDropPlaylist(playlistId)
        ? WriteAsync(new StagePlaylistForDevice(playlistId, Target.DeviceId, Target.ProfileId, Target.ProfileRevision,
            Plan?.PlanId, Plan?.PlanRevision), Apply, token, Accepts) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveStagedEntryAsync(ImmutableArray<Guid> entries, CancellationToken token) =>
        Plan is { PlanId: { } id, PlanRevision: { } revision } && !entries.IsDefaultOrEmpty
            ? WriteAsync(new EditDeviceExportPlan(id, new DeviceExportEdit.Remove(entries), revision), Apply, token,
                incoming => incoming.PlanId == id && Accepts(incoming)) : Task.CompletedTask;

    internal void ApplyPreview(DeviceExportPlan incoming)
    {
        if (incoming.DeviceId != Target.DeviceId)
            return;
        var next = new DeviceExportTarget(incoming.DeviceId, incoming.ProfileId, incoming.ProfileRevision);
        if (next != Target)
        {
            currentTarget = next;
            Activate(Scope.OwnerId);
            SetProperty(ref plan, null, nameof(Plan));
            OnPropertyChanged(nameof(Target));
        }
        if (Accepts(incoming))
            Apply(incoming);
    }

    protected override void OnWriteAvailabilityChanged()
    {
        AddSelectedCommand.NotifyCanExecuteChanged(); DropTracksCommand.NotifyCanExecuteChanged();
        DropSavedPlaylistCommand.NotifyCanExecuteChanged(); RemoveStagedEntryCommand.NotifyCanExecuteChanged();
    }

    private bool HasTarget => Target.DeviceId != Guid.Empty && !string.IsNullOrWhiteSpace(Target.ProfileId);
    private bool CanAddTracks(ImmutableArray<Guid> tracks) => CanWrite && HasTarget && !tracks.IsDefaultOrEmpty;
    private bool CanDropPlaylist(Guid playlistId) => CanWrite && HasTarget && playlistId != Guid.Empty;
    private bool CanRemove(ImmutableArray<Guid> entries) => CanWrite && Plan is { PlanId: not null, PlanRevision: not null } && !entries.IsDefaultOrEmpty;

    private Task StageTracksAsync(ImmutableArray<Guid> tracks, CancellationToken token) => CanAddTracks(tracks)
        ? WriteAsync(new StageTracksForDevice(tracks, Target.DeviceId, Target.ProfileId, Target.ProfileRevision,
            Plan?.PlanId, Plan?.PlanRevision), Apply, token, Accepts) : Task.CompletedTask;

    private bool Accepts(DeviceExportPlan incoming) => incoming.DeviceId == Target.DeviceId && incoming.ProfileId == Target.ProfileId &&
        incoming.ProfileRevision == Target.ProfileRevision && (incoming.PlanId is null) == (incoming.PlanRevision is null) &&
        (Plan?.PlanId is null || incoming.PlanId == Plan.PlanId && incoming.PlanRevision >= Plan.PlanRevision);

    private void Apply(DeviceExportPlan incoming)
    {
        SetProperty(ref plan, incoming, nameof(Plan));
        SelectedEntryIds = [.. SelectedEntryIds.Where(id => incoming.Entries.Any(entry => entry.EntryId == id))];
        OnWriteAvailabilityChanged();
    }
}
