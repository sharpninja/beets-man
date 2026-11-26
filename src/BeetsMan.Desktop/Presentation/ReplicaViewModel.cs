using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns registration, replication status, and resolution of offline edits.</summary>
/// <remarks>2026-11-24: Bind FR-RECORDS-001#ac-register, ac-replicate, ac-notify and ac-offline to typed requests.</remarks>
/// <remarks>2026-11-25: Use the reviewed revocation revision and expose the host's role and permissions for FR-RECORDS-001#ac-register.</remarks>
/// <remarks>2026-11-25: Retain the newest Last seen observation across roster replays for FR-RECORDS-001#ac-register.</remarks>
public sealed partial class ReplicaViewModel(PresentationServices services, Guid ownerId, Guid libraryId, Guid instanceId,
    ShellViewModel shell) : PresentationViewModel(services, ownerId)
{
    [ObservableProperty] private RegisteredInstancesSnapshot? instances;
    [ObservableProperty] private RegistrationReceipt? registration;
    [ObservableProperty] private ReplicationState? state;
    [ObservableProperty] private ReplicaConflictReceipt? resolution;

    [RelayCommand]
    private Task LoadInstancesAsync(CancellationToken token) => ReadAsync(new GetRegisteredInstances(libraryId), "instances",
        ApplyInstances, token, Accepts);

    [RelayCommand]
    private Task OpenRegistrationAsync(CancellationToken token) =>
        shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.RegisterInstance(libraryId), token);

    [RelayCommand]
    private Task OpenRevocationAsync(Guid selectedInstance, CancellationToken token) => Instances is { CanManage: true } registry &&
        selectedInstance != instanceId && registry.Instances.Any(item => item.InstanceId == selectedInstance && !item.Revoked)
            ? shell.OpenOverlayAsync(Scope.OwnerId, new OverlayIntent.RevokeInstance(selectedInstance, registry.Revision), token)
            : Task.CompletedTask;

    [RelayCommand]
    private Task RegisterAsync(RegistrationDraft draft, CancellationToken token)
    {
        var accepted = false;
        return WriteAsync(new RegisterInstance(libraryId, draft, Instances?.Revision ?? 0), incoming =>
        {
            accepted = true;
            Registration = incoming;
            var items = Instances?.Instances ?? [];
            var existing = items.FirstOrDefault(item => item.InstanceId == incoming.Instance.InstanceId);
            ApplyInstances(new(libraryId, incoming.Revision,
                existing is null ? items.Add(incoming.Instance) : items.Replace(existing, incoming.Instance))
                { CanManage = incoming.Instance.Role == DesktopRegistrationRole.Operator });
        }, token, incoming => incoming.LibraryId == libraryId && incoming.Instance.InstanceId == instanceId &&
            incoming.Revision >= (Instances?.Revision ?? 0), blocksInteraction: true,
            afterResult: () => accepted ? LoadInstancesAsync(token) : Task.CompletedTask);
    }

    [RelayCommand]
    private Task RevokeAsync(Guid selectedInstance, CancellationToken token)
    {
        if (Instances is not { CanManage: true } || shell.State is not { Overlays.IsDefaultOrEmpty: false } window ||
            window.Overlays[^1] is not { Intent: OverlayIntent.RevokeInstance approval } overlay ||
            overlay.OwnerId != Scope.OwnerId || approval.InstanceId != selectedInstance)
            return Task.CompletedTask;
        return WriteAsync(new RevokeInstance(libraryId, selectedInstance, approval.RegistryRevision),
            ApplyInstances, token, Accepts, blocksInteraction: true);
    }

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

    private void ApplyInstances(RegisteredInstancesSnapshot incoming)
    {
        if (Instances is { } current)
        {
            var known = current.Instances.ToDictionary(item => item.InstanceId, item => item.LastSeenUtc);
            incoming = incoming with
            {
                Instances = [.. incoming.Instances.Select(item =>
                    known.TryGetValue(item.InstanceId, out var observed) && observed is { } time &&
                    (item.LastSeenUtc is null || item.LastSeenUtc < time) ? item with { LastSeenUtc = time } : item)]
            };
        }
        Instances = incoming;
    }

    private bool Accepts(ReplicationState incoming) => incoming.LibraryId == libraryId && incoming.InstanceId == instanceId &&
        incoming.Revision >= (State?.Revision ?? 0);
}
