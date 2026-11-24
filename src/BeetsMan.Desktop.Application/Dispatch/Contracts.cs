namespace BeetsMan.Desktop.Application.Dispatch;

/// <summary>Describes a typed application mutation.</summary>
/// <remarks>2026-11-24: Materialize the FR-DISPATCH-001#ac-routing interface in cqrs.md.</remarks>
public interface ICommand<TResult>;

/// <summary>Describes a typed read of application state.</summary>
public interface IQuery<TResult>;

/// <summary>Identifies one dispatch attempt and its optional registered origin.</summary>
public readonly record struct RequestContext(Guid CorrelationId, Guid? OriginInstanceId);

/// <summary>Identifies a mutation and its stable replay identity.</summary>
public readonly record struct CommandContext(RequestContext Request, Guid OperationId);

/// <summary>Routes a typed request to its application handler and returns its outcome.</summary>
public interface IRequestDispatcher
{
    /// <summary>Executes one command attempt.</summary>
    Task<Outcome<TResult>> SendAsync<TResult>(ICommand<TResult> command, CommandContext context, CancellationToken cancellationToken);

    /// <summary>Reads a snapshot through the query handler.</summary>
    Task<Outcome<TResult>> QueryAsync<TResult>(IQuery<TResult> query, RequestContext context, CancellationToken cancellationToken);
}

/// <summary>Handles one concrete command and its declared result type.</summary>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    /// <summary>Executes the command using the supplied operation identity.</summary>
    Task<Outcome<TResult>> HandleAsync(TCommand command, CommandContext context, CancellationToken cancellationToken);
}

/// <summary>Handles one concrete query and its declared result type.</summary>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    /// <summary>Returns an immutable query snapshot or a typed failure.</summary>
    Task<Outcome<TResult>> HandleAsync(TQuery query, RequestContext context, CancellationToken cancellationToken);
}

/// <summary>Provides a stable validation code and optional affected input name.</summary>
public sealed record ValidationIssue(string Code, string? Field = null);

/// <summary>Represents the result of a command or query.</summary>
public abstract record Outcome<TResult>
{
    private Outcome() { }

    /// <summary>Contains the accepted command receipt or read snapshot.</summary>
    public sealed record Success(TResult Value) : Outcome<TResult>;

    /// <summary>Contains actionable validation issues.</summary>
    public sealed record Invalid(IReadOnlyList<ValidationIssue> Issues) : Outcome<TResult>;

    /// <summary>Describes a stale or incompatible attempt.</summary>
    public sealed record Conflict(string Code, long? ActualRevision) : Outcome<TResult>;

    /// <summary>Indicates cancellation before a committed result could be returned.</summary>
    public sealed record Cancelled : Outcome<TResult>;

    /// <summary>Identifies a dependency failure and whether the caller may retry the intent.</summary>
    public sealed record DependencyFailure(string Code, bool Retryable) : Outcome<TResult>;
}
