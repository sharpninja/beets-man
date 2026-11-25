using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Renders an existing export into a different validated player layout without assigning it yet.</summary>
/// <remarks>2026-11-24: Implement the STB20 private player-profile preview for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed class DeviceExportProfileEditor(IHostDeviceExportSources catalog, IHostExportPlaylistLookup playlists,
    IHostDeviceExportPresence presence, IDeviceExportPayloadStore payloads, IEnumerable<AndroidLayoutProfile> profiles,
    IEnumerable<IExportPlaylistFormatter> formatters)
{
    private readonly Dictionary<string, AndroidLayoutProfile> layouts = profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, IExportPlaylistFormatter> formats = formatters.ToDictionary(format => format.Format, StringComparer.Ordinal);

    /// <summary>Projects the selected profile, ordered playlist occurrences, and owned paths that would be replaced.</summary>
    internal async Task<HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>> PrepareAsync(DeviceExportPreviewSources inputs,
        DeviceExportPreviewIntent intent, CancellationToken token)
    {
        if (!layouts.TryGetValue(intent.ProfileId, out var profile))
            return Reject("export-profile-unavailable");
        if (profile.Version != intent.ProfileRevision)
            return new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Rejected("export-profile-changed", profile.Version, Conflict: true);

        var source = inputs.Plan;
        var plan = source with { ProfileId = profile.Id, ProfileRevision = profile.Version };
        var target = inputs.Destination;
        var comparer = target.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var priorPaths = DeviceExportLayoutEditor.Paths(source).ToLookup(item => item.Source, item => item.Path);
        var trackIds = priorPaths.Select(group => group.Key).Where(item => item.Kind == ExportContentKind.Audio)
            .Select(item => item.EntityId).ToArray();
        var issues = ImmutableArray.CreateBuilder<DeviceExportPreviewIssue>();
        var edits = intent.RelativePathsByTrack ?? ImmutableDictionary<Guid, string>.Empty;
        foreach (var (id, path) in edits)
        {
            if (!priorPaths.Contains(new(ExportContentKind.Audio, id)))
                issues.Add(new("export-layout-track-unavailable", id.ToString("D")));
            else if (!PlaylistExportPlanner.TryNormalize(path, out _))
                issues.Add(new("invalid-track-destination", path ?? id.ToString("D")));
        }
        if (issues.Count > 0)
            return Blocked(inputs with { Plan = plan }, issues, comparer);

        var read = await catalog.ReadDevicePlaylistSourcesAsync(target.LibraryId, target.DeviceId, trackIds, token).ConfigureAwait(false);
        if (read is not HostOutcome<DevicePlaylistSources>.Success { Value: var catalogInputs })
            return read.PropagateFailure<DeviceExportLayoutEditor.LayoutCorrection>();
        if (catalogInputs.Destination != target)
            return new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Rejected("export-registration-changed",
                catalogInputs.Destination.Revision, Conflict: true);
        var audioPaths = new Dictionary<Guid, string>();
        var desired = new Dictionary<string, ExportContentSource>(comparer);
        foreach (var id in trackIds)
        {
            if (!catalogInputs.Tracks.TryGetValue(id, out var track) || !HostExportMedia.TryNormalize(track.RelativePath, out var relative))
                return Reject("export-track-unavailable");
            var destination = edits.GetValueOrDefault(id) ??
                profile.ResolveAudioPath(track.AlbumArtist, track.Artist, track.Album, relative[(relative.LastIndexOf('/') + 1)..]);
            if (!PlaylistExportPlanner.TryNormalize(destination, out var path))
                return Reject("invalid-track-destination");
            audioPaths.Add(id, path);
            if (!desired.TryAdd(path, new(ExportContentKind.Audio, id)))
                issues.Add(new("export-destination-collision", path));
        }

        var playlistPaths = new Dictionary<Guid, string>();
        var sourcePlaylists = source.Entries.OfType<ExportEntry.Playlist>().ToArray();
        IExportPlaylistFormatter? formatter = null;
        if (sourcePlaylists.Length > 0 && (profile.PlaylistFormat is not { } format || !formats.TryGetValue(format, out formatter)))
            return Reject("unsupported-playlist-format");
        foreach (var playlist in sourcePlaylists)
        {
            var result = await playlists.ReadPlaylistAsync(target.LibraryId, playlist.PlaylistId, token).ConfigureAwait(false);
            if (result is not HostOutcome<SavedHostPlaylist?>.Success { Value: var saved })
                return result.PropagateFailure<DeviceExportLayoutEditor.LayoutCorrection>();
            if (saved is null || profile.ResolvePlaylistPath(saved.Name) is not { } path)
                return Reject("saved-playlist-unavailable");
            playlistPaths.Add(playlist.EntryId, path);
            if (!desired.TryAdd(path, new(ExportContentKind.Playlist, playlist.PlaylistId)))
                issues.Add(new("export-destination-collision", path));
        }
        foreach (var path in desired.Keys.Where(path => DeviceExportLayoutEditor.HasFileParent(path, desired)))
            issues.Add(new("export-destination-collision", path));
        if (issues.Count > 0)
            return Blocked(inputs with { Plan = plan }, issues, comparer);

        var observed = await presence.ReadAsync(target, audioPaths.Values, token).ConfigureAwait(false);
        if (observed is not HostOutcome<ImmutableDictionary<string, bool>>.Success { Value: var present })
            return observed.PropagateFailure<DeviceExportLayoutEditor.LayoutCorrection>();
        var owned = inputs.Manifest.Files.ToLookup(file => file.Source, file => file.Path);
        var resolved = new Dictionary<Guid, ExportTrackReference>();
        foreach (var (id, path) in audioPaths)
        {
            if (!present.TryGetValue(path, out var exists))
                return new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Unavailable("export-inventory-incomplete");
            var identity = new ExportContentSource(ExportContentKind.Audio, id);
            if (exists && !priorPaths[identity].Contains(path, comparer) && !owned[identity].Contains(path, comparer))
                issues.Add(new("export-destination-collision", path));
            resolved.Add(id, new(id, path, exists ? ExportAudioStatus.Present : ExportAudioStatus.Staged));
        }
        if (issues.Count > 0)
            return Blocked(inputs with { Plan = plan }, issues, comparer);

        var entries = ImmutableArray.CreateBuilder<ExportEntry>();
        var digests = ImmutableDictionary.CreateBuilder<Guid, string>();
        var audioRows = source.Entries.OfType<ExportEntry.Audio>().ToLookup(audio => audio.TrackId);
        var emitted = new HashSet<Guid>();
        foreach (var entry in source.Entries)
        {
            if (entry is ExportEntry.Audio audio)
            {
                var track = resolved[audio.TrackId];
                var direct = audioRows[audio.TrackId].Any(row => row.IsDirect);
                if (emitted.Add(audio.TrackId) && (direct || track.Status == ExportAudioStatus.Staged))
                    entries.Add(audio with { Destination = track.Destination, Status = track.Status, IsDirect = direct });
                continue;
            }
            if (entry is ExportEntry.Playlist playlist)
            {
                var path = playlistPaths[playlist.EntryId];
                var ordered = playlist.Tracks.Select(track => resolved[track.TrackId]).ToImmutableArray();
                var references = ordered.Select(track => PlaylistExportPlanner.RelativeToPlaylist(path, track.Destination, comparer));
                digests.Add(playlist.EntryId, await payloads.StoreAsync(formatter!.FormatTracks([.. references]), token).ConfigureAwait(false));
                entries.Add(playlist with { Destination = path, Tracks = ordered, Format = formatter.Format });
            }
        }
        foreach (var track in resolved.Values.Where(track => track.Status == ExportAudioStatus.Staged && !emitted.Contains(track.TrackId)))
            entries.Add(new ExportEntry.Audio(Guid.NewGuid(), track.Destination, track.TrackId, track.Status));

        var projected = plan with { Entries = entries.ToImmutable() };
        var digestMap = digests.ToImmutable();
        var projectedEntries = entries.Select(entry => new ReviewedDeviceExportEntry(entry, digestMap.GetValueOrDefault(entry.EntryId)));
        var selected = desired.Values.ToHashSet();
        var retire = inputs.Manifest.Files.Where(file => selected.Contains(file.Source) && !desired.ContainsKey(file.Path))
            .Select(file => file.Path).ToImmutableHashSet(comparer);
        return new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Success(new(
            inputs with { Plan = projected, PlaylistDigests = digestMap }, [.. projectedEntries], retire, []));
    }

    private static HostOutcome<DeviceExportLayoutEditor.LayoutCorrection> Blocked(DeviceExportPreviewSources inputs,
        ImmutableArray<DeviceExportPreviewIssue>.Builder issues, StringComparer comparer) =>
        new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Success(new(inputs, [], ImmutableHashSet.Create<string>(comparer), [.. issues.Distinct()]));

    private static HostOutcome<DeviceExportLayoutEditor.LayoutCorrection> Reject(string code) =>
        new HostOutcome<DeviceExportLayoutEditor.LayoutCorrection>.Rejected(code);
}
