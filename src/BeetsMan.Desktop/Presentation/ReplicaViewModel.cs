using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Application.Replication;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns registration, replication status, and resolution of offline edits.</summary>
/// <remarks>2026-11-24: Bind FR-RECORDS-001#ac-register, ac-replicate, ac-notify and ac-offline to typed requests.</remarks>
public sealed partial class ReplicaViewModel(PresentationServices services, Guid ownerId, Guid libraryId, Guid instanceId,
    ShellViewModel shell) : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private RegisteredInstancesSnapshot? instances;
    [ObservableProperty] private RegistrationReceipt? registration;
    [ObservableProperty] private ReplicationState? state;
    [ObservableProperty] private ReplicaConflictReceipt? resolution;

    [RelayCommand]
    private Task LoadInstancesAsync(CancellationToken token) => ReadAsync(new GetRegisteredInstances(libraryId), "instances",
        incoming => Instances = incoming, token, Accepts);

    [RelayCommand]
    private Task OpenRegistrationAsync(CancellationToken token) =>
        shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.RegisterInstance(libraryId), token);

    [RelayCommand]
    private Task OpenRevocationAsync(Guid selectedInstance, CancellationToken token) => Instances is { } registry &&
        registry.Instances.Any(item => item.InstanceId == selectedInstance && !item.Revoked)
            ? shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.RevokeInstance(selectedInstance, registry.Revision), token)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RegisterAsync(RegistrationDraft draft, CancellationToken token) =>
        WriteAsync(new RegisterInstance(libraryId, draft, Instances?.Revision ?? 0), incoming =>
        {
            Registration = incoming;
            var items = Instances?.Instances ?? [];
            if (!items.Any(item => item.InstanceId == incoming.Instance.InstanceId))
                Instances = new(libraryId, incoming.Revision, items.Add(incoming.Instance));
        }, token, incoming => incoming.LibraryId == libraryId && incoming.Revision >= (Instances?.Revision ?? 0));

    [RelayCommand]
    private Task RevokeAsync(Guid selectedInstance, CancellationToken token) => Instances is { } registry &&
        registry.Instances.Any(item => item.InstanceId == selectedInstance && !item.Revoked)
            ? WriteAsync(new RevokeInstance(libraryId, selectedInstance, registry.Revision), incoming => Instances = incoming, token, Accepts)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshStatusAsync(CancellationToken token) => ReadAsync(new GetReplicationStatus(libraryId, instanceId), "replication",
        incoming => State = incoming, token, Accepts);

    [RelayCommand]
    private Task RetrySyncAsync(CancellationToken token) => RecoverAsync(ReplicaRecoveryMode.Incremental, token);

    [RelayCommand]
    private Task RefreshSnapshotAsync(CancellationToken token) => RecoverAsync(ReplicaRecoveryMode.Snapshot, token);

    [RelayCommand]
    private Task KeepHostAsync(ReplicaConflict conflict, CancellationToken token) => ResolveAsync(conflict, ReplicaResolution.KeepHost, token);

    [RelayCommand]
    private Task ReapplyLocalAsync(ReplicaConflict conflict, CancellationToken token) => ResolveAsync(conflict, ReplicaResolution.ReapplyLocal, token);

    /// <summary>Applies the active replica's current checkpoint and pending state.</summary>
    public Task ReceiveAsync(ReplicationState incoming) => PostAsync(Scope, () =>
    {
        if (Accepts(incoming))
            State = incoming;
    });

    private Task RecoverAsync(ReplicaRecoveryMode mode, CancellationToken token) => State is { } current
        ? WriteAsync(new ResumeReplication(libraryId, instanceId, mode, current.Cursor), incoming => State = incoming, token, Accepts)
        : Task.CompletedTask;

    private Task ResolveAsync(ReplicaConflict conflict, ReplicaResolution choice, CancellationToken token) =>
        State is { } current && current.Conflicts.Contains(conflict)
            ? WriteAsync(new ResolveReplicaConflict(conflict.ConflictId, choice, conflict.HostRevision), incoming =>
            {
                Resolution = incoming;
                if (State is { } latest && latest.Conflicts.Contains(conflict))
                    State = latest with { Conflicts = latest.Conflicts.Remove(conflict) };
            }, token, incoming => incoming.ConflictId == conflict.ConflictId && incoming.Resolution == choice)
            : Task.CompletedTask;

    private bool Accepts(RegisteredInstancesSnapshot incoming) => incoming.LibraryId == libraryId && incoming.Revision >= (Instances?.Revision ?? 0);
    private bool Accepts(ReplicationState incoming) => incoming.LibraryId == libraryId && incoming.InstanceId == instanceId &&
        incoming.Revision >= (State?.Revision ?? 0);
}
