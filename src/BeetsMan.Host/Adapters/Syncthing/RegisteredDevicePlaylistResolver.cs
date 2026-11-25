using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Gets a receiver API key through the host credential service.</summary>
/// <remarks>2026-11-24: Keep Syncthing API keys out of the registration document for FR-FILESYNC-001#ac-safety.</remarks>
public interface IRegisteredSyncthingTokenProvider
{
    /// <summary>Returns a private receiver credential for exactly the registered library and device.</summary>
    ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token);
}

/// <summary>Reads the exact registered receiver inventory using its private credential.</summary>
/// <remarks>2026-11-24: Share scoped receiver lookups between playlist drop and layout correction for FR-FILESYNC-001#ac-safety.</remarks>
public sealed class RegisteredDeviceExportPresence(SyncthingReceiverInventory inventory, IRegisteredSyncthingTokenProvider credentials,
    ISyncthingExportSenderRegistry? senders = null) : IHostDeviceExportPresence, IAndroidShareStatus
{
    /// <inheritdoc />
    public async Task<HostOutcome<ImmutableDictionary<string, bool>>> ReadAsync(RegisteredAndroidExport target,
        IEnumerable<string> paths, CancellationToken token)
    {
        var credential = await credentials.GetTokenAsync(target.LibraryId, target.DeviceId, token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential))
            return new HostOutcome<ImmutableDictionary<string, bool>>.Unavailable("export-inventory-credentials-unavailable");
        return await inventory.ReadAsync(SyncthingReceiver.FromRegistration(target, senders), paths, credential, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    async Task<HostOutcome<AndroidShareCondition>> IAndroidShareStatus.ReadShareStatusAsync(RegisteredAndroidExport target, CancellationToken token)
    {
        var credential = await credentials.GetTokenAsync(target.LibraryId, target.DeviceId, token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(credential) ? new HostOutcome<AndroidShareCondition>.Unavailable("export-receiver-credentials-unavailable") :
            await inventory.ReadShareStatusAsync(SyncthingReceiver.FromRegistration(target, senders), credential, token).ConfigureAwait(false);
    }
}

/// <summary>Resolves host-owned catalog paths against a registered player's layout and Android receiver.</summary>
/// <remarks>2026-11-24: Bind the typed device staging operation to real catalog and receiver adapters for FR-FILESYNC-001#ac-layouts, ac-playlist-drop and ac-safety.</remarks>
public sealed class RegisteredDevicePlaylistResolver : IHostDevicePlaylistResolver, IHostDirectTrackResolver
{
    private readonly IHostDeviceExportSources sources;
    private readonly IHostDeviceExportPresence presence;
    private readonly Dictionary<string, AndroidLayoutProfile> profiles;
    private readonly HostExportMedia media;
    private readonly ISyncthingExportSenderRegistry? senderRegistry;

    /// <summary>Configures the resolver with validated player profiles and the authorized media root.</summary>
    public RegisteredDevicePlaylistResolver(IHostDeviceExportSources sources, SyncthingReceiverInventory inventory,
        IRegisteredSyncthingTokenProvider credentials, IEnumerable<AndroidLayoutProfile> profiles, string musicRoot,
        ISyncthingExportSenderRegistry? senderRegistry = null)
    {
        this.sources = sources;
        presence = new RegisteredDeviceExportPresence(inventory, credentials, senderRegistry);
        this.profiles = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        media = new(musicRoot);
        this.senderRegistry = senderRegistry;
    }

    /// <inheritdoc />
    public async Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token)
    {
        if (!profiles.TryGetValue(request.ProfileId, out var profile))
            return new HostOutcome<ResolvedDevicePlaylist>.Rejected("export-profile-unavailable");
        if (profile.Version != request.ProfileRevision)
            return new HostOutcome<ResolvedDevicePlaylist>.Rejected("export-profile-changed", profile.Version, Conflict: true);
        if (profile.PlaylistFormat is not { } format || profile.ResolvePlaylistPath(playlist.Name) is not { } playlistPath)
            return new HostOutcome<ResolvedDevicePlaylist>.Rejected("unsupported-playlist-format");

        var input = await sources.ReadDevicePlaylistSourcesAsync(playlist.LibraryId, request.DeviceId,
            playlist.Entries.Select(entry => entry.TrackId), token).ConfigureAwait(false);
        if (input is not HostOutcome<DevicePlaylistSources>.Success { Value: var resolved })
            return input.PropagateFailure<ResolvedDevicePlaylist>();
        var tracks = await ResolvePathsAsync(resolved, profile, null, token).ConfigureAwait(false);
        return tracks is HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Success { Value: var byId }
            ? new HostOutcome<ResolvedDevicePlaylist>.Success(new(resolved.Destination,
                new(playlist.PlaylistId, resolved.Destination.DeviceId, profile.Id, profile.Version, format, playlistPath,
                    resolved.Destination.CaseSensitivePaths, [.. playlist.Entries.Select(entry => byId[entry.TrackId])])))
            : tracks.PropagateFailure<ResolvedDevicePlaylist>();
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Resolve STB10 direct drops using the same registered inventory and path validation as saved playlists.</remarks>
    public async Task<HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>> ResolveTracksAsync(DevicePlaylistSources sources,
        StageDeviceTracksRequest request, DeviceExportPlan? plan, CancellationToken token)
    {
        if (!profiles.TryGetValue(request.ProfileId, out var profile))
            return new HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>.Rejected("export-profile-unavailable");
        if (profile.Version != request.ProfileRevision)
            return new HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>.Rejected("export-profile-changed", profile.Version, Conflict: true);
        var lookup = (plan?.Entries ?? []).SelectMany(entry => entry switch
        {
            ExportEntry.Audio audio => [new ExportTrackReference(audio.TrackId, audio.Destination, audio.Status)],
            ExportEntry.Playlist playlist => playlist.Tracks,
            _ => ImmutableArray<ExportTrackReference>.Empty
        }).ToLookup(track => track.TrackId, track => track.Destination);
        var resolved = await ResolvePathsAsync(sources, profile, lookup, token).ConfigureAwait(false);
        return resolved is HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Success { Value: var byId }
            ? new HostOutcome<ImmutableArray<ResolvedPlaylistTrack>>.Success([.. request.OrderedTrackIds.Distinct().Select(id => byId[id])])
            : resolved.PropagateFailure<ImmutableArray<ResolvedPlaylistTrack>>();
    }

    private async Task<HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>> ResolvePathsAsync(DevicePlaylistSources sources,
        AndroidLayoutProfile profile, ILookup<Guid, string>? prior, CancellationToken token)
    {
        var target = sources.Destination;
        if (senderRegistry is not null && senderRegistry.Find(target) is null)
            return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Rejected("export-share-unconfigured");
        var comparer = target.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var paths = new Dictionary<Guid, (string Source, string Destination)>();
        foreach (var (trackId, track) in sources.Tracks)
        {
            if (!HostExportMedia.TryNormalize(track.RelativePath, out var source) ||
                profile.ResolveAudioPath(track.AlbumArtist, track.Artist, track.Album, source[(source.LastIndexOf('/') + 1)..]) is not { } destination)
                return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Rejected("invalid-track-destination");
            var priorPaths = prior?[trackId].Distinct(comparer).Take(2).ToArray() ?? [];
            if (priorPaths.Length == 1)
                destination = priorPaths[0];
            else if (priorPaths.Length > 1)
                return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Rejected("export-layout-track-ambiguous");
            if (!PlaylistExportPlanner.TryNormalize(destination, out var safeDestination))
                return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Rejected("invalid-track-destination");
            paths.Add(trackId, (source, safeDestination));
        }

        var inventory = await presence.ReadAsync(target, paths.Values.Select(path => path.Destination), token).ConfigureAwait(false);
        if (inventory is not HostOutcome<ImmutableDictionary<string, bool>>.Success { Value: var present })
            return inventory.PropagateFailure<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>();

        var tracks = ImmutableDictionary.CreateBuilder<Guid, ResolvedPlaylistTrack>();
        foreach (var (trackId, path) in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!present.TryGetValue(path.Destination, out var alreadyOnDevice))
                return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Unavailable("export-inventory-incomplete");
            using var source = alreadyOnDevice ? null : media.Open(path.Source);
            tracks.Add(trackId, new(trackId, path.Destination, alreadyOnDevice, source is not null));
        }
        return new HostOutcome<ImmutableDictionary<Guid, ResolvedPlaylistTrack>>.Success(tracks.ToImmutable());
    }

}
