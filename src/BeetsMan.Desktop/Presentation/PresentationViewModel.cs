using BeetsMan.Desktop.Application.Dispatch;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Classifies feedback from the current scoped attempt.</summary>
public enum PresentationFailureKind { Invalid, Conflict, Dependency }

/// <summary>Contains the typed information displayed for an unsuccessful application action.</summary>
public sealed record PresentationFailure(PresentationFailureKind Kind, RequestStamp Stamp, string? Code,
    IReadOnlyList<ValidationIssue> Issues, long? ActualRevision = null)
{
    internal Func<CancellationToken, Task>? Retry { get; init; }

    /// <summary>Indicates whether the original typed intent can be submitted again.</summary>
    public bool CanRetry => Retry is not null;
}

/// <summary>Projects scoped CQRS results for generated Toolkit commands.</summary>
/// <remarks>2026-11-24: Route typed intents, reject superseded results and keep independent actions usable under FR-DISPATCH-001#ac-routing, ac-update and ac-responsive.</remarks>
public abstract class PresentationViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<string, long> latestQueries = new(StringComparer.Ordinal);
    private CancellationTokenSource lifetime = new();
    private long sequence;
    private bool active = true;
    private bool disposed;
    private bool isInteractionBlocked;
    private PresentationFailure? failure;

    protected PresentationViewModel(PresentationServices services, Guid ownerId)
    {
        Services = services;
        Scope = new(ownerId, 0);
        services.WriteChanged += HandleWriteChanged;
    }

    /// <summary>Gets the current scope used by independent jobs and notifications.</summary>
    public PresentationScope Scope { get; private set; }

    /// <summary>Gets whether the owning presentation still accepts commands and results.</summary>
    public bool IsActive => active;

    /// <summary>Gets whether a mutation is pending for the default write owner.</summary>
    public bool IsWriting => Services.HasWrite(WriteOwner);

    /// <summary>Gets whether the owning UI must show a system wait cursor while it is noninteractive.</summary>
    public bool IsInteractionBlocked
    {
        get => isInteractionBlocked;
        private set => SetProperty(ref isInteractionBlocked, value);
    }

    /// <summary>Gets the last failure accepted by this presentation scope.</summary>
    public PresentationFailure? Failure
    {
        get => failure;
        private set => SetProperty(ref failure, value);
    }

    protected PresentationServices Services { get; }
    protected virtual Guid WriteOwner => Scope.OwnerId;
    protected bool CanWrite => IsActive && !IsWriting;

    /// <summary>Starts a fresh presentation epoch on the owning UI thread.</summary>
    public void Activate(Guid ownerId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Invalidate();
        lifetime.Dispose();
        lifetime = new();
        Scope = new(ownerId, Scope.Epoch + 1);
        active = true;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsActive));
        HandleWriteChanged(WriteOwner);
    }

    /// <summary>Stops accepting work and cancels pending scope-owned attempts on the UI thread.</summary>
    public void Deactivate() => Invalidate();

    /// <summary>Detaches the ViewModel from its presentation services.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Invalidate();
        Services.WriteChanged -= HandleWriteChanged;
        lifetime.Dispose();
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDisposed() { }
    protected virtual void OnWriteAvailabilityChanged() { }

    protected void ClearFailure() => Failure = null;

    protected Task ReadAsync<TResult>(IQuery<TResult> request, string slot, Action<TResult> apply,
        CancellationToken token, Predicate<TResult>? accepts = null) =>
        ReadAttemptAsync(request, slot, apply, token, accepts);

    /// <summary>Dispatches a scoped write, optionally reusing the identity of an earlier uncertain attempt.</summary>
    /// <remarks>2026-11-24: Preserve transfer retry identity for FR-FILESYNC-001#ac-safety.</remarks>
    protected Task WriteAsync<TResult>(ICommand<TResult> request, Action<TResult> apply, CancellationToken token,
        Predicate<TResult>? accepts = null, bool retryInvalid = false, bool blocksInteraction = false, Guid? writeOwner = null, Guid? operationId = null) =>
        WriteAttemptAsync(request, operationId ?? Guid.NewGuid(), apply, token, accepts, retryInvalid, blocksInteraction, writeOwner ?? WriteOwner);

    protected Task PostAsync(PresentationScope scope, Action apply) =>
        Services.Scheduler.PostAsync(() =>
        {
            if (active && !disposed && Scope == scope)
                apply();
        }, CancellationToken.None);

    protected async Task DispatchEventAsync<TResult>(ICommand<TResult> request, Action<TResult> apply,
        Predicate<TResult> accepts, CancellationToken token)
    {
        if (!active || token.IsCancellationRequested)
            return;
        var context = Services.NewRequest();
        var stamp = new RequestStamp(Scope, context.CorrelationId, ++sequence);
        var operationId = Guid.NewGuid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        var result = await ObserveCancellationAsync(ct => Services.Dispatcher.SendAsync(request,
            new(context, operationId), ct), linked.Token).ConfigureAwait(false);
        await PostAsync(stamp.Scope, () =>
        {
            if (result is Outcome<TResult>.Success success && accepts(success.Value))
                apply(success.Value);
        }).ConfigureAwait(false);
    }

    private async Task ReadAttemptAsync<TResult>(IQuery<TResult> request, string slot, Action<TResult> apply,
        CancellationToken token, Predicate<TResult>? accepts)
    {
        if (!active || token.IsCancellationRequested)
            return;

        var context = Services.NewRequest();
        var stamp = new RequestStamp(Scope, context.CorrelationId, ++sequence);
        latestQueries[slot] = stamp.Sequence;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        var result = await ObserveCancellationAsync(
            ct => Services.Dispatcher.QueryAsync(request, context, ct), linked.Token).ConfigureAwait(false);

        await PostAsync(stamp.Scope, () =>
        {
            if (latestQueries.GetValueOrDefault(slot) != stamp.Sequence)
                return;
            ApplyOutcome(result, stamp, apply, accepts, retryInvalid: false, ct =>
                IsCurrent(stamp, slot) ? ReadAttemptAsync(request, slot, apply, ct, accepts) : Task.CompletedTask);
        }).ConfigureAwait(false);
    }

    private async Task WriteAttemptAsync<TResult>(ICommand<TResult> request, Guid operationId, Action<TResult> apply,
        CancellationToken token, Predicate<TResult>? accepts, bool retryInvalid, bool blocksInteraction, Guid writeOwner)
    {
        if (!active || token.IsCancellationRequested || !Services.TryEnterWrite(writeOwner))
            return;

        var context = Services.NewRequest();
        var stamp = new RequestStamp(Scope, context.CorrelationId, ++sequence);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
        if (blocksInteraction)
            IsInteractionBlocked = true;

        Outcome<TResult>? result = null;
        try
        {
            result = await ObserveCancellationAsync(
                ct => Services.Dispatcher.SendAsync(request, new(context, operationId), ct), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await Services.Scheduler.PostAsync(() =>
            {
                try
                {
                    if (result is not null && active && Scope == stamp.Scope)
                        ApplyOutcome(result, stamp, apply, accepts, retryInvalid, ct =>
                            active && Scope == stamp.Scope && Failure?.Stamp == stamp
                                ? WriteAttemptAsync(request, operationId, apply, ct, accepts, retryInvalid, blocksInteraction, writeOwner)
                                : Task.CompletedTask);
                }
                finally
                {
                    if (blocksInteraction && Scope == stamp.Scope)
                        IsInteractionBlocked = false;
                    Services.LeaveWrite(writeOwner);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<Outcome<TResult>> ObserveCancellationAsync<TResult>(
        Func<CancellationToken, Task<Outcome<TResult>>> dispatch, CancellationToken token)
    {
        try
        {
            return await dispatch(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<TResult>.Cancelled();
        }
    }

    private void ApplyOutcome<TResult>(Outcome<TResult> result, RequestStamp stamp, Action<TResult> apply,
        Predicate<TResult>? accepts, bool retryInvalid, Func<CancellationToken, Task> retry)
    {
        if (result is Outcome<TResult>.Success success)
        {
            if (accepts?.Invoke(success.Value) != false)
            {
                apply(success.Value);
                if (Failure is { } existing && existing.Stamp.Sequence <= stamp.Sequence)
                    Failure = null;
            }
            return;
        }

        if (Failure is { } current && current.Stamp.Sequence > stamp.Sequence)
            return;

        Failure = result switch
        {
            Outcome<TResult>.Invalid invalid => new(PresentationFailureKind.Invalid, stamp, null, [.. invalid.Issues])
                { Retry = retryInvalid ? retry : null },
            Outcome<TResult>.Conflict conflict => new(PresentationFailureKind.Conflict, stamp, conflict.Code, [], conflict.ActualRevision),
            Outcome<TResult>.DependencyFailure dependency => new(PresentationFailureKind.Dependency, stamp, dependency.Code, [])
                { Retry = dependency.Retryable ? retry : null },
            Outcome<TResult>.Cancelled => null,
            _ => throw new InvalidOperationException("Unhandled CQRS outcome.")
        };
    }

    private bool IsCurrent(RequestStamp stamp, string slot) => active && Scope == stamp.Scope &&
        Failure?.Stamp == stamp && latestQueries.GetValueOrDefault(slot) == stamp.Sequence;

    private void Invalidate()
    {
        if (!active)
            return;
        active = false;
        latestQueries.Clear();
        lifetime.Cancel();
        Failure = null;
        IsInteractionBlocked = false;
        OnPropertyChanged(nameof(IsActive));
        OnWriteAvailabilityChanged();
    }

    private void HandleWriteChanged(Guid owner)
    {
        if (owner != WriteOwner)
            return;
        OnPropertyChanged(nameof(IsWriting));
        OnWriteAvailabilityChanged();
    }
}
