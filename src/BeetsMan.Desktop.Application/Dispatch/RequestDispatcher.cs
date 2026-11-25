using System.Collections.Frozen;

namespace BeetsMan.Desktop.Application.Dispatch;

/// <summary>Routes commands and queries by concrete request type through an immutable registration table.</summary>
/// <remarks>2026-11-24: Implement FR-DISPATCH-001#ac-routing and the typed handler boundary in TR-DISPATCH-ARCH-001.</remarks>
public sealed class RequestDispatcher : IRequestDispatcher
{
    private readonly FrozenDictionary<Type, object> routes;

    private RequestDispatcher(FrozenDictionary<Type, object> routes) => this.routes = routes;

    /// <inheritdoc />
    public Task<Outcome<TResult>> SendAsync<TResult>(ICommand<TResult> command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var handler = Resolve<CommandRoute<TResult>>(command.GetType());
        return ExecuteAsync(token => handler.Invoke(command, context, token), cancellationToken);
    }

    /// <inheritdoc />
    public Task<Outcome<TResult>> QueryAsync<TResult>(IQuery<TResult> query, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var handler = Resolve<QueryRoute<TResult>>(query.GetType());
        return ExecuteAsync(token => handler.Invoke(query, context, token), cancellationToken);
    }

    private TRoute Resolve<TRoute>(Type request) => routes.GetValueOrDefault(request) is TRoute route
        ? route : throw new InvalidOperationException($"No handler registration matches {request.FullName} and its declared result.");

    private static async Task<Outcome<TResult>> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<Outcome<TResult>>> execute, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return new Outcome<TResult>.Cancelled();
        try
        {
            return await execute(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<TResult>.Cancelled();
        }
    }

    private sealed record CommandRoute<TResult>(Func<ICommand<TResult>, CommandContext, CancellationToken, Task<Outcome<TResult>>> Invoke);
    private sealed record QueryRoute<TResult>(Func<IQuery<TResult>, RequestContext, CancellationToken, Task<Outcome<TResult>>> Invoke);

    /// <summary>Registers each handler exactly once and checks the required composition before use.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<Type, object> registrations = [];

        /// <summary>Registers the handler for a concrete command and its outcome.</summary>
        public Builder RegisterCommand<TCommand, TResult>(ICommandHandler<TCommand, TResult> handler)
            where TCommand : ICommand<TResult>
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(typeof(TCommand), new CommandRoute<TResult>((request, context, token) => handler.HandleAsync((TCommand)request, context, token)));
            return this;
        }

        /// <summary>Registers the handler for a concrete query and its outcome.</summary>
        public Builder RegisterQuery<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
            where TQuery : IQuery<TResult>
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(typeof(TQuery), new QueryRoute<TResult>((request, context, token) => handler.HandleAsync((TQuery)request, context, token)));
            return this;
        }

        /// <summary>Creates an immutable routing table for the selected startup or normal composition.</summary>
        public RequestDispatcher Build(params Type[] required)
        {
            ArgumentNullException.ThrowIfNull(required);
            foreach (var request in required)
            {
                if (!registrations.ContainsKey(request))
                    throw new InvalidOperationException($"Missing required handler for {request.FullName}.");
            }
            return new RequestDispatcher(registrations.ToFrozenDictionary());
        }

        private void Register(Type request, object route)
        {
            if (request.IsAbstract || request.IsInterface || request.ContainsGenericParameters)
                throw new ArgumentException($"Handler registrations require a concrete request: {request.FullName}.");
            if (!registrations.TryAdd(request, route))
                throw new InvalidOperationException($"A handler is already registered for {request.FullName}.");
        }
    }
}
