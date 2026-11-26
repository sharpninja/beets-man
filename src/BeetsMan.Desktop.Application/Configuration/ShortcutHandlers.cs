using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Stores the per-desktop overrides at a single local revision.</summary>
/// <remarks>2026-11-25: Preserve contextual edits for FR-SETTINGS-001#ac-shortcuts.</remarks>
public sealed record StoredShortcuts(long Revision, ImmutableArray<ShortcutBinding> Overrides);

/// <summary>Reads and atomically replaces local shortcut overrides.</summary>
public interface IShortcutStore
{
    Task<Outcome<StoredShortcuts>> ReadShortcutsAsync(CancellationToken token);
    Task<Outcome<StoredShortcuts>> WriteShortcutsAsync(long expectedRevision, ImmutableArray<ShortcutBinding> bindings, CancellationToken token);
}

/// <summary>Applies one complete OS playback key registration or preserves the previous set on failure.</summary>
/// <remarks>2026-11-25: Keep platform registration separate from in-app mappings for FR-SETTINGS-001#ac-shortcuts.</remarks>
public interface ISystemPlaybackShortcutPlatform : IAsyncDisposable
{
    /// <summary>Reports an activation of one binding owned by the current registration.</summary>
    event Action<ShortcutBinding>? Activated;

    /// <summary>Reports a change made by the operating system to the active registrations.</summary>
    /// <remarks>2026-11-25: Project portal revocation for FR-SETTINGS-001#ac-shortcuts.</remarks>
    event Action? RegistrationChanged { add { } remove { } }

    /// <summary>Exposes live bindings when the platform can change or revoke a prior registration.</summary>
    /// <remarks>2026-11-25: A null observation uses the apply receipt for FR-SETTINGS-001#ac-shortcuts.</remarks>
    ImmutableArray<ShortcutBinding>? ActiveBindings => null;

    /// <summary>Replaces the desktop's owned registrations. An empty set releases them.</summary>
    Task<Outcome<bool>> ApplyAsync(ImmutableArray<ShortcutBinding> bindings, CancellationToken token);
}

/// <summary>Owns the contextual shortcut requests and the separate opt-in platform registration.</summary>
/// <remarks>2026-11-25: Route conflict previews, explicit assignment, reset and OS denial for FR-SETTINGS-001#ac-shortcuts.</remarks>
public sealed class ShortcutHandlers(IShortcutStore store, ISystemPlaybackShortcutPlatform? platform = null)
    : IQueryHandler<GetShortcutBindings, ShortcutSnapshot>, IQueryHandler<ResolveContextualShortcut, ShortcutResolution>,
      ICommandHandler<AssignShortcut, ShortcutSnapshot>, ICommandHandler<ResetShortcuts, ShortcutSnapshot>,
      ICommandHandler<SetGlobalPlaybackShortcuts, ShortcutSnapshot>, IAsyncDisposable
{
    private readonly SemaphoreSlim systemLane = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object systemStateGate = new();
    private SystemRegistration registration = new(0, [], []);
    private bool systemApplying;
    private int disposed;

    /// <summary>Identifies the current registration for a platform activation, if the binding is still registered.</summary>
    /// <remarks>2026-11-25: Scope queued playback shortcut delivery for FR-SETTINGS-001#ac-shortcuts.</remarks>
    public long? CaptureSystemActivation(ShortcutBinding binding)
    {
        var current = ReadSystem();
        return Volatile.Read(ref disposed) == 0 && current.Requested.Contains(binding) ? current.Revision : null;
    }

    /// <summary>Registers all local and platform shortcut requests.</summary>
    public void Register(RequestDispatcher.Builder builder) => builder.RegisterQuery<GetShortcutBindings, ShortcutSnapshot>(this)
        .RegisterQuery<ResolveContextualShortcut, ShortcutResolution>(this)
        .RegisterCommand<AssignShortcut, ShortcutSnapshot>(this).RegisterCommand<ResetShortcuts, ShortcutSnapshot>(this)
        .RegisterCommand<SetGlobalPlaybackShortcuts, ShortcutSnapshot>(this);

    /// <inheritdoc />
    public Task<Outcome<ShortcutSnapshot>> HandleAsync(GetShortcutBindings query, RequestContext context, CancellationToken token)
    {
        var gesture = ShortcutCatalog.NormalizeGesture(query.Gesture);
        if (!IsEditorScope(query.Scope) || query.Gesture is not null && (gesture is null || !ShortcutScopes.IsContext(query.Scope)))
            return Task.FromResult(Invalid<ShortcutSnapshot>("invalid-shortcut-scope-or-gesture"));
        return ReadAsync(current => Task.FromResult(Snapshot(query.Scope, current, gesture)), token);
    }

    /// <inheritdoc />
    public Task<Outcome<ShortcutResolution>> HandleAsync(ResolveContextualShortcut query, RequestContext context, CancellationToken token)
    {
        if (query.ActiveScopes.IsDefaultOrEmpty || query.ActiveScopes.Length > 3 ||
            query.ActiveScopes.Any(scope => !ShortcutScopes.IsContext(scope)) ||
            query.ActiveScopes.Distinct(StringComparer.Ordinal).Count() != query.ActiveScopes.Length ||
            ShortcutCatalog.NormalizeGesture(query.Gesture) is not { } gesture)
            return Task.FromResult(Invalid<ShortcutResolution>("invalid-shortcut-scope-or-gesture"));
        return ReadAsync(current =>
        {
            var bindings = Effective(current);
            var winner = query.ActiveScopes.OrderBy(scope => scope == ShortcutScopes.Dialog ? 0 : 1)
                .Select(scope => bindings.FirstOrDefault(binding => binding.Scope == scope && binding.Gesture == gesture))
                .FirstOrDefault(binding => binding is not null);
            return Task.FromResult<Outcome<ShortcutResolution>>(new Outcome<ShortcutResolution>.Success(new(current.Revision, winner)));
        }, token);
    }

    /// <inheritdoc />
    public Task<Outcome<ShortcutSnapshot>> HandleAsync(AssignShortcut command, CommandContext context, CancellationToken token)
    {
        if (!ShortcutCatalog.AllowedInContext(command.ActionId, command.Scope) ||
            ShortcutCatalog.NormalizeGesture(command.Gesture) is not { } gesture)
            return Task.FromResult(Invalid<ShortcutSnapshot>("invalid-shortcut-assignment"));
        return EditAsync(command.Scope, command.ExpectedRevision, current =>
        {
            var collision = Effective(current).FirstOrDefault(binding => binding.Scope == command.Scope &&
                binding.Gesture == gesture && binding.ActionId != command.ActionId);
            if (collision is not null)
                return new Outcome<ImmutableArray<ShortcutBinding>>.Invalid([new("shortcut-gesture-in-use", collision.ActionId)]);
            var assigned = new ShortcutBinding(command.ActionId, command.Scope, gesture);
            var overrides = current.Overrides.Where(binding => (binding.ActionId, binding.Scope) != (command.ActionId, command.Scope));
            if (!ShortcutCatalog.Defaults.Contains(assigned))
                overrides = overrides.Append(assigned);
            return new Outcome<ImmutableArray<ShortcutBinding>>.Success([.. overrides.OrderBy(binding => binding.Scope, StringComparer.Ordinal)
                .ThenBy(binding => binding.ActionId, StringComparer.Ordinal)]);
        }, token);
    }

    /// <inheritdoc />
    public Task<Outcome<ShortcutSnapshot>> HandleAsync(ResetShortcuts command, CommandContext context, CancellationToken token) =>
        !IsEditorScope(command.Scope) ? Task.FromResult(Invalid<ShortcutSnapshot>("invalid-shortcut-scope")) :
        EditAsync(command.Scope, command.ExpectedRevision, current => new Outcome<ImmutableArray<ShortcutBinding>>.Success(
            command.Scope == ShortcutScopes.All ? [] : [.. current.Overrides.Where(binding => binding.Scope != command.Scope)]), token);

    /// <inheritdoc />
    public async Task<Outcome<ShortcutSnapshot>> HandleAsync(SetGlobalPlaybackShortcuts command, CommandContext context, CancellationToken token)
    {
        if (!IsEditorScope(command.Scope) || NormalizeSystem(command) is not { } requested)
            return Invalid<ShortcutSnapshot>("invalid-system-playback-shortcuts");
        try
        {
            await systemLane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref disposed) != 0)
                    return new Outcome<ShortcutSnapshot>.Cancelled();
                SystemRegistration current;
                lock (systemStateGate)
                {
                    current = ReadSystem();
                    systemApplying = true;
                }
                if (current.Revision != command.ExpectedRevision)
                    return new Outcome<ShortcutSnapshot>.Conflict("stale-system-shortcuts", current.Revision);
                var stored = await store.ReadShortcutsAsync(token).ConfigureAwait(false);
                if (stored is not Outcome<StoredShortcuts>.Success local)
                    return Forward<StoredShortcuts, ShortcutSnapshot>(stored);
                if (current.Requested.SequenceEqual(requested))
                    return Snapshot(command.Scope, local.Value);
                if (platform is null)
                    return new Outcome<ShortcutSnapshot>.DependencyFailure("system-playback-shortcuts-unavailable", false);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                Outcome<bool> applied;
                try { applied = await platform.ApplyAsync(requested, attempt.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return new Outcome<ShortcutSnapshot>.Cancelled(); }
                if (applied is not Outcome<bool>.Success receipt)
                    return Forward<bool, ShortcutSnapshot>(applied);
                if (!receipt.Value)
                    return new Outcome<ShortcutSnapshot>.DependencyFailure("system-playback-shortcut-denied", false);
                var observed = platform.ActiveBindings ?? requested;
                lock (systemStateGate)
                {
                    registration = new(current.Revision + 1, observed.IsEmpty ? [] : requested, observed);
                    systemApplying = false;
                }
                var refreshed = await store.ReadShortcutsAsync(CancellationToken.None).ConfigureAwait(false);
                return Snapshot(command.Scope, refreshed is Outcome<StoredShortcuts>.Success latest ? latest.Value : local.Value);
            }
            finally
            {
                lock (systemStateGate)
                    systemApplying = false;
                systemLane.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<ShortcutSnapshot>.Cancelled(); }
    }

    /// <summary>Joins an in-flight platform request and releases the active workspace's registrations.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        await systemLane.WaitAsync().ConfigureAwait(false);
        try
        {
            if (platform is not null)
                await platform.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lifetime.Dispose();
            systemLane.Release();
        }
    }

    private Task<Outcome<ShortcutSnapshot>> EditAsync(string scope, long expectedRevision,
        Func<StoredShortcuts, Outcome<ImmutableArray<ShortcutBinding>>> edit, CancellationToken token) => ReadAsync(async current =>
    {
        if (current.Revision != expectedRevision)
            return new Outcome<ShortcutSnapshot>.Conflict("stale-shortcut-bindings", current.Revision);
        var changed = edit(current);
        if (changed is not Outcome<ImmutableArray<ShortcutBinding>>.Success next)
            return Forward<ImmutableArray<ShortcutBinding>, ShortcutSnapshot>(changed);
        if (current.Overrides.SequenceEqual(next.Value))
            return Snapshot(scope, current);
        var saved = await store.WriteShortcutsAsync(expectedRevision, next.Value, token).ConfigureAwait(false);
        return saved is Outcome<StoredShortcuts>.Success committed ? Snapshot(scope, committed.Value) :
            Forward<StoredShortcuts, ShortcutSnapshot>(saved);
    }, token);

    private async Task<Outcome<TResult>> ReadAsync<TResult>(Func<StoredShortcuts, Task<Outcome<TResult>>> apply, CancellationToken token)
    {
        var loaded = await store.ReadShortcutsAsync(token).ConfigureAwait(false);
        return loaded is Outcome<StoredShortcuts>.Success current ? await apply(current.Value).ConfigureAwait(false) :
            Forward<StoredShortcuts, TResult>(loaded);
    }

    private Outcome<ShortcutSnapshot> Snapshot(string scope, StoredShortcuts current, string? gesture = null)
    {
        var bindings = Effective(current).Where(binding => scope == ShortcutScopes.All || binding.Scope == scope).ToImmutableArray();
        var system = ReadSystem();
        var winning = gesture is null ? null : bindings.FirstOrDefault(binding => binding.Gesture == gesture);
        return new Outcome<ShortcutSnapshot>.Success(new(scope, current.Revision, bindings, !system.Actual.IsEmpty,
            winning, system.Revision, system.Actual));
    }

    private SystemRegistration ReadSystem()
    {
        lock (systemStateGate)
        {
            if (!systemApplying && !registration.Requested.IsEmpty && platform?.ActiveBindings is { } observed &&
                !registration.Actual.SequenceEqual(observed))
                registration = new(registration.Revision + 1, observed.IsEmpty ? [] : registration.Requested, observed);
            return registration;
        }
    }

    private static IEnumerable<ShortcutBinding> Effective(StoredShortcuts current) => ShortcutCatalog.Defaults
        .Where(builtIn => !current.Overrides.Any(binding => (binding.ActionId, binding.Scope) == (builtIn.ActionId, builtIn.Scope)))
        .Concat(current.Overrides).OrderBy(binding => binding.Scope, StringComparer.Ordinal).ThenBy(binding => binding.ActionId, StringComparer.Ordinal);

    private static bool IsEditorScope(string? scope) => scope == ShortcutScopes.All || ShortcutScopes.IsContext(scope);

    private static ImmutableArray<ShortcutBinding>? NormalizeSystem(SetGlobalPlaybackShortcuts command)
    {
        if (!command.Enabled)
            return [];
        var proposed = command.RequestedBindings.IsDefaultOrEmpty ? ShortcutCatalog.DefaultSystemBindings : command.RequestedBindings;
        if (proposed.Length > 4)
            return null;
        var byAction = new HashSet<string>(StringComparer.Ordinal);
        var byGesture = new HashSet<string>(StringComparer.Ordinal);
        var normalized = ImmutableArray.CreateBuilder<ShortcutBinding>(proposed.Length);
        foreach (var binding in proposed)
        {
            if (binding is null || binding.Scope != ShortcutScopes.System || !ShortcutCatalog.AllowedGlobally(binding.ActionId) ||
                ShortcutCatalog.NormalizeGesture(binding.Gesture) is not { } gesture || !byAction.Add(binding.ActionId) || !byGesture.Add(gesture))
                return null;
            normalized.Add(binding with { Gesture = gesture, SystemTriggerDescription = null });
        }
        return [.. normalized.OrderBy(binding => binding.ActionId, StringComparer.Ordinal)];
    }

    private static Outcome<TResult> Invalid<TResult>(string code) => new Outcome<TResult>.Invalid([new(code)]);

    private static Outcome<TResult> Forward<T, TResult>(Outcome<T> source) => source switch
    {
        Outcome<T>.Invalid invalid => new Outcome<TResult>.Invalid(invalid.Issues),
        Outcome<T>.Conflict conflict => new Outcome<TResult>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<T>.DependencyFailure failure => new Outcome<TResult>.DependencyFailure(failure.Code, failure.Retryable),
        _ => new Outcome<TResult>.Cancelled()
    };

    private sealed record SystemRegistration(long Revision, ImmutableArray<ShortcutBinding> Requested, ImmutableArray<ShortcutBinding> Actual);
}
