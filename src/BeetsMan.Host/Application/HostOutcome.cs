namespace BeetsMan.Host.Application;

/// <summary>Separates committed host operations from authorization, validation, and storage failures.</summary>
/// <remarks>2026-11-24: Return typed host results for FR-RECORDS-001#ac-register and ac-replicate.</remarks>
public abstract record HostOutcome<T>
{
    private HostOutcome() { }

    /// <summary>Contains a committed receipt or detached read result.</summary>
    public sealed record Success(T Value) : HostOutcome<T>;

    /// <summary>Indicates an authenticated caller is not permitted to act in the requested library.</summary>
    public sealed record Forbidden : HostOutcome<T>;

    /// <summary>Identifies a validation or revision conflict.</summary>
    public sealed record Rejected(string Code, long? ActualRevision = null, bool Conflict = false) : HostOutcome<T>;

    /// <summary>Indicates cancellation before the result was committed.</summary>
    public sealed record Cancelled : HostOutcome<T>;

    /// <summary>Identifies a storage or dependency failure.</summary>
    public sealed record Unavailable(string Code) : HostOutcome<T>;

    /// <summary>Preserves a failed outcome when an operation returns a different success type.</summary>
    /// <remarks>2026-11-24: Preserve validation and dependency codes across FR-FILESYNC-001#ac-playlist-drop steps.</remarks>
    internal HostOutcome<TResult> PropagateFailure<TResult>() => this switch
    {
        Forbidden => new HostOutcome<TResult>.Forbidden(),
        Rejected rejected => new HostOutcome<TResult>.Rejected(rejected.Code, rejected.ActualRevision, rejected.Conflict),
        Cancelled => new HostOutcome<TResult>.Cancelled(),
        Unavailable unavailable => new HostOutcome<TResult>.Unavailable(unavailable.Code),
        _ => throw new InvalidOperationException("A successful value cannot be propagated as a failure.")
    };
}

/// <summary>The library and instance identity verified at the host boundary.</summary>
/// <remarks>2026-11-25: Carry the host-provisioned Operator authority for FR-RECORDS-001#ac-register.</remarks>
public sealed record AuthenticatedHostInstance(Guid LibraryId, Guid InstanceId, bool CanManageRegistrations = false);
