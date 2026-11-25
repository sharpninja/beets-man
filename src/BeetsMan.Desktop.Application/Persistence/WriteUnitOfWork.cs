using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Persistence;

/// <summary>Provides the typed repositories for one atomic, synchronous write callback.</summary>
/// <remarks>2026-11-24: Materialize the repository transaction contract for TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
public interface IWriteUnitOfWork<TRepositories>
{
    /// <summary>Commits a successful callback and rolls back a rejected or cancelled callback.</summary>
    Task<Outcome<TResult>> ExecuteAsync<TResult>(Func<TRepositories, Outcome<TResult>> write, CancellationToken cancellationToken);
}
