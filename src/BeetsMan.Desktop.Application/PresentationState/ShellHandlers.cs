using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.PresentationState;

/// <summary>Validates a live authenticated Android connection against current registered peers.</summary>
/// <remarks>2026-11-24: Define the application event boundary for FR-FILESYNC-001#ac-arrival.</remarks>
public interface IAndroidConnectionRegistry
{
    /// <summary>Returns whether the generation is live and belongs to a registered Android peer.</summary>
    bool IsLiveRegisteredConnection(Guid peerId, long generation);
}

/// <summary>Serializes process-local shell mutations and preserves playback and modal ownership.</summary>
/// <remarks>2026-11-24: Implement FR-PLAYER-001#ac-modes, FR-FILESYNC-001#ac-arrival and FR-DISPATCH-001#ac-responsive.</remarks>
public sealed class ShellHandlers(ShellState initial, IAndroidConnectionRegistry connections) :
    ICommandHandler<NavigateWorkspace, ShellState>, ICommandHandler<OpenOverlay, ShellState>,
    ICommandHandler<CloseOverlay, ShellState>, ICommandHandler<ChangePresentation, ShellState>,
    ICommandHandler<ApplyAndroidPeerArrival, ShellState>
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, long> acceptedGenerations = [];
    private ShellState state = initial.Overlays.IsDefault ? initial with { Overlays = [] } : initial;

    /// <summary>Registers the window's shell handlers in the application's routing table.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterCommand<NavigateWorkspace, ShellState>(this)
        .RegisterCommand<OpenOverlay, ShellState>(this)
        .RegisterCommand<CloseOverlay, ShellState>(this)
        .RegisterCommand<ChangePresentation, ShellState>(this)
        .RegisterCommand<ApplyAndroidPeerArrival, ShellState>(this);

    /// <inheritdoc />
    public Task<Outcome<ShellState>> HandleAsync(NavigateWorkspace command, CommandContext context, CancellationToken token) =>
        Mutate(command.WindowId, token, current =>
            !Enum.IsDefined(command.Route.Workspace) || command.Route.PeerId is not null && command.Route.Workspace != Workspace.Sync
                ? Invalid("invalid-workspace-route")
                : Accept(current with { Route = command.Route }));

    /// <inheritdoc />
    public Task<Outcome<ShellState>> HandleAsync(OpenOverlay command, CommandContext context, CancellationToken token) =>
        Mutate(command.WindowId, token, current => command.OwnerId == Guid.Empty
            ? Invalid("overlay-owner-required")
            : Accept(current with { Overlays = current.Overlays.Add(new(Guid.NewGuid(), command.OwnerId, command.Intent, current.FocusTarget)) }));

    /// <inheritdoc />
    public Task<Outcome<ShellState>> HandleAsync(CloseOverlay command, CommandContext context, CancellationToken token) =>
        Mutate(command.WindowId, token, current =>
        {
            if (current.Overlays.IsDefaultOrEmpty || current.Overlays[^1].OverlayId != command.OverlayId)
                return new Outcome<ShellState>.Conflict("overlay-no-longer-active", current.Revision);
            var overlay = current.Overlays[^1];
            return Accept(current with { Overlays = current.Overlays.RemoveAt(current.Overlays.Length - 1), FocusTarget = overlay.FocusTarget });
        });

    /// <inheritdoc />
    public Task<Outcome<ShellState>> HandleAsync(ChangePresentation command, CommandContext context, CancellationToken token) =>
        Mutate(command.WindowId, token, current =>
        {
            var next = command.ModeOrRestore.Mode ?? current.PriorWindowedMode;
            if (!Enum.IsDefined(next))
                return Invalid("invalid-presentation-mode");
            var prior = next == PresentationMode.FullVisualization ? current.PriorWindowedMode : next;
            return Accept(current with { Mode = next, PriorWindowedMode = prior });
        });

    /// <inheritdoc />
    public Task<Outcome<ShellState>> HandleAsync(ApplyAndroidPeerArrival command, CommandContext context, CancellationToken token) =>
        Mutate(command.WindowId, token, current =>
        {
            if (command.PeerId == Guid.Empty || command.ConnectionGeneration < 0 ||
                acceptedGenerations.TryGetValue(command.PeerId, out var generation) && command.ConnectionGeneration <= generation ||
                !connections.IsLiveRegisteredConnection(command.PeerId, command.ConnectionGeneration))
                return new Outcome<ShellState>.Success(current);

            acceptedGenerations[command.PeerId] = command.ConnectionGeneration;
            return Accept(current with
            {
                Route = new(Workspace.Sync, command.PeerId),
                Mode = PresentationMode.Normal,
                PriorWindowedMode = PresentationMode.Normal
            });
        });

    private Task<Outcome<ShellState>> Mutate(Guid window, CancellationToken token, Func<ShellState, Outcome<ShellState>> transition)
    {
        lock (gate)
        {
            var result = token.IsCancellationRequested ? new Outcome<ShellState>.Cancelled() :
                window != state.WindowId ? Invalid("wrong-window") : transition(state);
            return Task.FromResult(result);
        }
    }

    private Outcome<ShellState> Accept(ShellState next)
    {
        if (next != state)
            state = next with { Revision = checked(state.Revision + 1) };
        return new Outcome<ShellState>.Success(state);
    }

    private static Outcome<ShellState> Invalid(string code) => new Outcome<ShellState>.Invalid([new(code)]);
}
