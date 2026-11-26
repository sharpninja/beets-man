using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Identifies an enabled worker action and captures its declarative input.</summary>
/// <remarks>2026-11-25: Route contribution requests through CQRS for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record InvokePluginAction(Guid SessionId, string PluginId, string Version, string ContributionId,
    ImmutableDictionary<string, string> Arguments) : ICommand<PluginActionReceipt>;

/// <summary>Returns bounded worker text and the authoritative outcome of an optional host action.</summary>
public sealed record PluginActionReceipt(string PluginId, string Version, string ContributionId, string Message, PlaybackState? Playback = null);

/// <summary>Contains the worker result before a requested host effect has received authorization.</summary>
public sealed record PluginWorkerActionResult(string Message, string? HostAction);

/// <summary>Lists host actions available as explicit package grants.</summary>
public static class PluginHostActions
{
    public const string PausePlayback = "playback.pause";
}

/// <summary>Allows only bounded inputs and typed host effects for the current desktop session.</summary>
/// <remarks>2026-11-25: Recheck the live worker and its exact capability before dispatch for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed partial class PluginActionHandlers(PluginWorkerSupervisor supervisor, Guid sessionId, Func<IRequestDispatcher> dispatcher)
    : ICommandHandler<InvokePluginAction, PluginActionReceipt>
{
    /// <summary>Registers the contribution command on the desktop dispatcher.</summary>
    public void Register(RequestDispatcher.Builder builder) => builder.RegisterCommand<InvokePluginAction, PluginActionReceipt>(this)
        .RegisterQuery<GetPluginPanel, PluginPanelSnapshot>(this).RegisterCommand<SubmitPluginPanel, PluginActionReceipt>(this);

    /// <inheritdoc />
    public Task<Outcome<PluginActionReceipt>> HandleAsync(InvokePluginAction command, CommandContext context, CancellationToken token) =>
        ExecuteAsync(command, context, null, token);

    private Task<Outcome<PluginActionReceipt>> ExecuteAsync(InvokePluginAction command, CommandContext context, PluginPanelReference? panel,
        CancellationToken token)
    {
        if (command.SessionId != sessionId || context.OperationId == Guid.Empty || !PluginRegistryHandlers.IsIdentifier(command.PluginId) ||
            !PluginRegistryHandlers.IsVersion(command.Version) || !PluginRegistryHandlers.IsIdentifier(command.ContributionId) ||
            command.Arguments is not { Count: <= 16 } arguments || arguments.Any(item =>
                !PluginRegistryHandlers.IsIdentifier(item.Key) || item.Value is not { Length: <= 1024 } || item.Value.Contains('\0')))
            return Task.FromResult(Invalid("invalid-plugin-action"));
        return supervisor.InvokeActionAsync(command, context.OperationId, panel, ApplyAsync, token);

        async Task<Outcome<PluginActionReceipt>> ApplyAsync(PluginContribution definition, PluginWorkerActionResult reply)
        {
            var receipt = new PluginActionReceipt(command.PluginId, command.Version, command.ContributionId, reply.Message);
            if (reply.HostAction is null)
                return new Outcome<PluginActionReceipt>.Success(receipt);
            if (definition.HostAction != PluginHostActions.PausePlayback || reply.HostAction != definition.HostAction)
                return Invalid("plugin-host-action-denied");
            var applied = await dispatcher().SendAsync(new Transport(sessionId, TransportAction.Pause), context, token).ConfigureAwait(false);
            return applied is Outcome<PlaybackState>.Success playback ? new Outcome<PluginActionReceipt>.Success(receipt with { Playback = playback.Value }) :
                Forward(applied);
        }
    }

    internal static Outcome<PluginActionReceipt> Invalid(string code) => new Outcome<PluginActionReceipt>.Invalid([new(code)]);

    internal static Outcome<PluginActionReceipt> Forward<T>(Outcome<T> result) => result switch
    {
        Outcome<T>.Invalid invalid => new Outcome<PluginActionReceipt>.Invalid(invalid.Issues),
        Outcome<T>.Conflict conflict => new Outcome<PluginActionReceipt>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<T>.DependencyFailure failure => new Outcome<PluginActionReceipt>.DependencyFailure(failure.Code, failure.Retryable),
        _ => new Outcome<PluginActionReceipt>.Cancelled()
    };
}
