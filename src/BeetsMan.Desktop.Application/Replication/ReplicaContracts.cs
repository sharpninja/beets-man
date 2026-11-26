using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Replication;

/// <summary>Contains redacted registration data and a credential-store handle.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-RECORDS-001#ac-register, ac-replicate, ac-notify, and ac-offline.</remarks>
public sealed record RegistrationDraft(string Name, Uri Host, Guid CredentialSelectionToken);

/// <summary>Describes one registered instance visible to an authorized user.</summary>
/// <remarks>2026-11-25: Project the host assigned role for FR-RECORDS-001#ac-register.</remarks>
/// <remarks>2026-11-25: Project the optional host Last seen observation for FR-RECORDS-001#ac-register.</remarks>
public sealed record RegisteredInstance(Guid InstanceId, string Name, bool Revoked)
{
    public DesktopRegistrationRole Role { get; init; }

    /// <summary>Gets the most recent retained authenticated activity reported by the host.</summary>
    public DateTimeOffset? LastSeenUtc { get; init; }
}

/// <summary>Returns the registry for one library at a consistent revision.</summary>
/// <remarks>2026-11-25: Project the current caller's Operator permission for FR-RECORDS-001#ac-register.</remarks>
public sealed record RegisteredInstancesSnapshot(Guid LibraryId, long Revision, ImmutableArray<RegisteredInstance> Instances)
{
    public bool CanManage { get; init; }
}

/// <summary>Identifies the accepted registration.</summary>
public sealed record RegistrationReceipt(Guid LibraryId, RegisteredInstance Instance, long Revision);

/// <summary>Distinguishes incremental delivery from replacing an expired snapshot.</summary>
public enum ReplicaRecoveryMode { Incremental, Snapshot }

/// <summary>Identifies the available replica conflict resolutions.</summary>
public enum ReplicaResolution { KeepHost, ReapplyLocal }

/// <summary>Describes an unresolved local draft and the current host revision.</summary>
public sealed record ReplicaConflict(Guid ConflictId, Guid RecordId, long HostRevision, string HostSummary, string LocalSummary);

/// <summary>Contains committed replica state and its available recovery options.</summary>
public sealed record ReplicationState(Guid LibraryId, Guid InstanceId, long Revision, long Cursor, int PendingCount, bool SnapshotRequired, ImmutableArray<ReplicaConflict> Conflicts, Guid? RecoveryOperationId = null);

/// <summary>Describes the recorded outcome of resolving a local conflict.</summary>
public sealed record ReplicaConflictReceipt(Guid ConflictId, ReplicaResolution Resolution, long? CommittedHostRevision);

/// <summary>Reads registered desktop identities.</summary>
public sealed record GetRegisteredInstances(Guid LibraryId) : IQuery<RegisteredInstancesSnapshot>;

/// <summary>Accepts a new registered instance.</summary>
public sealed record RegisterInstance(Guid LibraryId, RegistrationDraft Draft, long ExpectedRevision) : ICommand<RegistrationReceipt>;

/// <summary>Revokes one versioned registration.</summary>
public sealed record RevokeInstance(Guid LibraryId, Guid InstanceId, long ExpectedRevision) : ICommand<RegisteredInstancesSnapshot>;

/// <summary>Reads current replica status.</summary>
public sealed record GetReplicationStatus(Guid LibraryId, Guid InstanceId) : IQuery<ReplicationState>;

/// <summary>Resumes delivery or starts a replacement snapshot.</summary>
public sealed record ResumeReplication(Guid LibraryId, Guid InstanceId, ReplicaRecoveryMode RecoveryMode, long ExpectedCursor) : ICommand<ReplicationState>;

/// <summary>Refreshes one registered replica from its durable cursor without initiating a snapshot.</summary>
/// <remarks>2026-11-25: Drive independent automatic delivery for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
public sealed record RefreshReplication(Guid LibraryId, Guid InstanceId) : ICommand<ReplicationState>;

/// <summary>Resolves one recorded conflict against the displayed version.</summary>
public sealed record ResolveReplicaConflict(Guid ConflictId, ReplicaResolution Resolution, long ExpectedHostRevision) : ICommand<ReplicaConflictReceipt>;
