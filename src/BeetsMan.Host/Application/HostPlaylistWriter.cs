using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Authorizes and publishes playlist revisions with a replayable receipt.</summary>
/// <remarks>2026-11-24: Implement the host save transaction for FR-RECORDS-001#ac-register, ac-replicate, ac-notify, ac-offline and FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed class HostPlaylistWriter(IHostPlaylistUnitOfWork writes)
{
    /// <summary>Commits a new playlist or an edit at the caller's expected revision.</summary>
    public Task<HostOutcome<PlaylistSaveResponse>> SaveAsync(Guid libraryId, AuthenticatedHostInstance caller, PlaylistSaveRequest command,
        CancellationToken cancellationToken)
    {
        if (libraryId == Guid.Empty || caller.LibraryId != libraryId || caller.InstanceId == Guid.Empty ||
            command.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<PlaylistSaveResponse>>(new HostOutcome<PlaylistSaveResponse>.Forbidden());
        if (!IsValid(command))
            return Task.FromResult<HostOutcome<PlaylistSaveResponse>>(new HostOutcome<PlaylistSaveResponse>.Rejected("invalid-playlist-save"));

        var key = new HostOperationKey(caller.InstanceId, command.OperationId);
        var fingerprint = Fingerprint(libraryId, command);
        return writes.ExecuteAsync(repositories => Apply(repositories, libraryId, key, command, fingerprint), cancellationToken);
    }

    private static HostOutcome<PlaylistSaveResponse> Apply(IHostPlaylistWriteRepositories repositories, Guid libraryId, HostOperationKey key,
        PlaylistSaveRequest command, string fingerprint)
    {
        if (!repositories.Registry.IsActive(libraryId, key.OriginInstanceId))
            return new HostOutcome<PlaylistSaveResponse>.Forbidden();
        if (repositories.Operations.Find(key) is { } previous)
            return previous.IntentFingerprint == fingerprint && previous.Receipt is { } recorded
                ? new HostOutcome<PlaylistSaveResponse>.Success(recorded)
                : new HostOutcome<PlaylistSaveResponse>.Rejected("operation-intent-mismatch", Conflict: true);

        SavedHostPlaylist playlist;
        if (command.PlaylistId is { } playlistId)
        {
            if (repositories.Publications.IsPlaylistReserved(libraryId, playlistId))
                return new HostOutcome<PlaylistSaveResponse>.Rejected("playlist-reserved-by-batch", Conflict: true);
            var current = repositories.Playlists.Find(libraryId, playlistId);
            if (current is null || current.Revision != command.ExpectedRevision)
                return new HostOutcome<PlaylistSaveResponse>.Rejected("stale-saved-playlist", current?.Revision, Conflict: true);
            playlist = current with { Name = command.Name, Entries = command.Entries, Revision = checked(current.Revision + 1) };
            if (!repositories.Playlists.TryReplace(playlist, current.Revision))
                return new HostOutcome<PlaylistSaveResponse>.Rejected("stale-saved-playlist", current.Revision, Conflict: true);
        }
        else
        {
            playlist = new(libraryId, Guid.NewGuid(), 1, command.Name, command.Entries);
            repositories.Playlists.Add(playlist);
        }

        var feedRevision = repositories.Publications.Append(playlist, key);
        var receipt = new PlaylistSaveResponse(key.OperationId, command.Source, playlist.PlaylistId, playlist.Revision, feedRevision);
        repositories.Operations.Add(key, new(fingerprint, receipt));
        return new HostOutcome<PlaylistSaveResponse>.Success(receipt);
    }

    /// <summary>Checks the playlist intent for FR-RECORDS-001#ac-replicate and FR-LIBRARY-002#ac-batch-commit.</summary>
    internal static bool IsValid(PlaylistSaveRequest command) =>
        command.OperationId != Guid.Empty && command.Source is { SourceId: var source } && source != Guid.Empty &&
        Enum.IsDefined(command.Source.Kind) && !string.IsNullOrWhiteSpace(command.Name) && !command.Entries.IsDefault &&
        (command.PlaylistId is { } playlistId ? playlistId != Guid.Empty && command.ExpectedRevision is > 0 : !command.ExpectedRevision.HasValue) &&
        (command.Source.Kind != PlaylistSaveSourceKind.Queue || command.PlaylistId.HasValue) &&
        command.Entries.All(entry => entry is not null && entry.EntryId != Guid.Empty && entry.TrackId != Guid.Empty) &&
        command.Entries.Select(entry => entry.EntryId).Distinct().Count() == command.Entries.Length;

    private static string Fingerprint(Guid libraryId, PlaylistSaveRequest command)
    {
        var fields = new { Schema = 1, Operation = "save-playlist", LibraryId = libraryId,
            command.Source, command.PlaylistId, command.ExpectedRevision, command.Name, command.Entries };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
    }
}
