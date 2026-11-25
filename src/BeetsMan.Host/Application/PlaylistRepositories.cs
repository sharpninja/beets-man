using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Captures one saved playlist with the host-owned revision and stable entry identities.</summary>
/// <remarks>2026-11-24: Materialize host-owned playlist state for TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
public sealed record SavedHostPlaylist(Guid LibraryId, Guid PlaylistId, long Revision, string Name, ImmutableArray<PlaylistEntryData> Entries);

/// <summary>Identifies a submitted operation independently of retry or correlation.</summary>
public readonly record struct HostOperationKey(Guid OriginInstanceId, Guid OperationId);

/// <summary>Contains the canonical intent fingerprint and original committed receipt.</summary>
public sealed record SavedHostPlaylistOperation(string IntentFingerprint, PlaylistSaveResponse? Receipt);

/// <summary>Reads and changes saved playlists inside the transaction.</summary>
public interface ISavedHostPlaylistRepository
{
    SavedHostPlaylist? Find(Guid libraryId, Guid playlistId);
    void Add(SavedHostPlaylist playlist);
    bool TryReplace(SavedHostPlaylist playlist, long expectedRevision);
}

/// <summary>Reads and records the accepted operation identity in the same transaction.</summary>
public interface IHostPlaylistOperationRepository
{
    SavedHostPlaylistOperation? Find(HostOperationKey key);
    void Add(HostOperationKey key, SavedHostPlaylistOperation operation);
}

/// <summary>Checks an authenticated instance against the current registration.</summary>
public interface IHostPlaylistRegistry
{
    bool IsActive(Guid libraryId, Guid instanceId);
}

/// <summary>Records the change and its delivery recipients at one new feed revision.</summary>
public interface IHostPlaylistPublicationRepository
{
    long Append(SavedHostPlaylist playlist, HostOperationKey origin);
}

/// <summary>Gives each playlist transaction access to the typed repositories it needs.</summary>
public interface IHostPlaylistWriteRepositories
{
    ISavedHostPlaylistRepository Playlists { get; }
    IHostPlaylistOperationRepository Operations { get; }
    IHostPlaylistRegistry Registry { get; }
    IHostPlaylistPublicationRepository Publications { get; }
}

/// <summary>Commits successful host writes and rolls back failed or cancelled operations.</summary>
public interface IHostPlaylistUnitOfWork
{
    Task<HostOutcome<T>> ExecuteAsync<T>(Func<IHostPlaylistWriteRepositories, HostOutcome<T>> write, CancellationToken cancellationToken);
}

/// <summary>Describes the host publication visible at one library revision.</summary>
public sealed record HostPlaylistPublication(long CurrentRevision, SavedHostPlaylist? Change, HostOperationKey? Origin,
    ImmutableArray<Guid> PendingRecipients);
