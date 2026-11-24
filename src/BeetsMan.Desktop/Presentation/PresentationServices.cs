using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Schedules application-driven state changes on the presentation thread.</summary>
/// <remarks>2026-11-24: Establish the scheduler seam for FR-DISPATCH-001#ac-update and TEST-DISPATCH-001.</remarks>
public interface IUiScheduler
{
    /// <summary>Applies a callback on the presentation thread and observes its completion.</summary>
    Task PostAsync(Action apply, CancellationToken cancellationToken);
}

/// <summary>Identifies the lifetime of one presentation owner.</summary>
public readonly record struct PresentationScope(Guid OwnerId, long Epoch);

/// <summary>Identifies an application attempt within its owning projection.</summary>
public readonly record struct RequestStamp(PresentationScope Scope, Guid CorrelationId, long Sequence);

/// <summary>Shares the application entry points and presentation write gates across ViewModels.</summary>
public sealed class PresentationServices(IRequestDispatcher dispatcher, IUiScheduler scheduler, Guid? originInstanceId = null)
{
    private readonly HashSet<Guid> writes = [];

    /// <summary>Gets the typed application dispatcher supplied by the composition root.</summary>
    public IRequestDispatcher Dispatcher { get; } = dispatcher;

    /// <summary>Gets the scheduler for bound state.</summary>
    public IUiScheduler Scheduler { get; } = scheduler;

    internal event Action<Guid>? WriteChanged;

    internal RequestContext NewRequest() => new(Guid.NewGuid(), originInstanceId);
    internal bool HasWrite(Guid owner) => writes.Contains(owner);

    internal bool TryEnterWrite(Guid owner)
    {
        if (!writes.Add(owner))
            return false;
        WriteChanged?.Invoke(owner);
        return true;
    }

    internal void LeaveWrite(Guid owner)
    {
        writes.Remove(owner);
        WriteChanged?.Invoke(owner);
    }
}
