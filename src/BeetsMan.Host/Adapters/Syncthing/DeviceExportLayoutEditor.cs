using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Prepares edited destinations and private playlist payloads for an existing player profile.</summary>
/// <remarks>2026-11-24: Bind the STB20 path-correction review to FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed class DeviceExportLayoutEditor(IHostDeviceExportPresence presence, IDeviceExportPayloadStore payloads,
    IEnumerable<IExportPlaylistFormatter> formatters)
{
    private readonly Dictionary<string, IExportPlaylistFormatter> formats = formatters.ToDictionary(format => format.Format, StringComparer.OrdinalIgnoreCase);

    internal async Task<HostOutcome<LayoutCorrection>> PrepareAsync(DeviceExportPreviewSources inputs,
        ImmutableDictionary<Guid, string> edits, CancellationToken token)
    {
        var target = inputs.Destination;
        var comparer = target.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var current = Paths(inputs.Plan).Where(path => path.Source.Kind == ExportContentKind.Audio)
            .ToLookup(path => path.Source.EntityId, path => path.Path);
        var changed = new Dictionary<Guid, string>();
        var issues = ImmutableArray.CreateBuilder<DeviceExportPreviewIssue>();
        foreach (var (trackId, path) in edits.OrderBy(pair => pair.Key))
        {
            if (!current.Contains(trackId))
                issues.Add(new("export-layout-track-unavailable", trackId.ToString("D")));
            else if (!PlaylistExportPlanner.TryNormalize(path, out var normalized))
                issues.Add(new("invalid-track-destination", string.IsNullOrWhiteSpace(path) ? current[trackId].First() : path));
            else if (current[trackId].Any(original => !comparer.Equals(original, normalized)))
                changed.Add(trackId, normalized);
        }
        if (issues.Count > 0)
            return Blocked(inputs, issues.ToImmutable(), comparer);
        if (changed.Count == 0)
            return new HostOutcome<LayoutCorrection>.Success(new(inputs, [], ImmutableHashSet.Create<string>(comparer), []));

        var entries = ImmutableArray.CreateBuilder<ExportEntry>();
        var projectedAudio = new HashSet<Guid>();
        var direct = inputs.Plan.Entries.OfType<ExportEntry.Audio>().Where(audio => audio.IsDirect).Select(audio => audio.TrackId).ToHashSet();
        foreach (var entry in inputs.Plan.Entries)
        {
            switch (entry)
            {
                case ExportEntry.Audio audio when changed.TryGetValue(audio.TrackId, out var path):
                    if (projectedAudio.Add(audio.TrackId))
                        entries.Add(audio with { Destination = path, Status = ExportAudioStatus.Staged, IsDirect = direct.Contains(audio.TrackId) });
                    break;
                case ExportEntry.Audio audio:
                    entries.Add(audio);
                    break;
                case ExportEntry.Playlist playlist:
                    entries.Add(playlist with { Tracks = [.. playlist.Tracks.Select(track => changed.TryGetValue(track.TrackId, out var path)
                        ? track with { Destination = path, Status = ExportAudioStatus.Staged } : track)] });
                    break;
            }
        }
        var added = entries.OfType<ExportEntry.Audio>().Select(audio => audio.TrackId).ToHashSet();
        foreach (var (trackId, path) in changed)
        {
            if (!added.Contains(trackId))
                entries.Add(new ExportEntry.Audio(Guid.NewGuid(), path, trackId, ExportAudioStatus.Staged));
        }

        var editedPlan = inputs.Plan with { Entries = entries.ToImmutable() };
        var proposed = new Dictionary<string, ExportContentSource>(comparer);
        foreach (var path in Paths(editedPlan))
        {
            if (!proposed.TryAdd(path.Path, path.Source) && proposed[path.Path] != path.Source)
                issues.Add(new("export-destination-collision", path.Path));
        }
        foreach (var path in proposed.Keys)
        {
            if (HasFileParent(path, proposed))
                issues.Add(new("export-destination-collision", path));
        }
        if (issues.Count > 0)
            return Blocked(inputs, [.. issues.Distinct()], comparer);

        var inventory = await presence.ReadAsync(target, changed.Values, token).ConfigureAwait(false);
        if (inventory is not HostOutcome<ImmutableDictionary<string, bool>>.Success { Value: var present })
            return inventory.PropagateFailure<LayoutCorrection>();
        foreach (var (trackId, path) in changed)
        {
            if (!present.TryGetValue(path, out var exists))
                return new HostOutcome<LayoutCorrection>.Unavailable("export-inventory-incomplete");
            if (exists && !inputs.Manifest.Files.Any(file => file.Source == new ExportContentSource(ExportContentKind.Audio, trackId) && comparer.Equals(file.Path, path)))
                issues.Add(new("export-destination-collision", path));
        }
        if (issues.Count > 0)
            return Blocked(inputs, issues.ToImmutable(), comparer);

        var digests = inputs.PlaylistDigests.ToBuilder();
        var original = inputs.Plan.Entries.OfType<ExportEntry.Playlist>().ToDictionary(playlist => playlist.EntryId);
        foreach (var playlist in editedPlan.Entries.OfType<ExportEntry.Playlist>())
        {
            if (playlist.Tracks.SequenceEqual(original[playlist.EntryId].Tracks))
                continue;
            if (!formats.TryGetValue(playlist.Format, out var formatter))
                return new HostOutcome<LayoutCorrection>.Rejected("unsupported-playlist-format");
            var relative = playlist.Tracks.Select(track => PlaylistExportPlanner.RelativeToPlaylist(playlist.Destination, track.Destination, comparer));
            var content = formatter.FormatTracks([.. relative]);
            digests[playlist.EntryId] = await payloads.StoreAsync(content, token).ConfigureAwait(false);
        }

        var retire = inputs.Manifest.Files.Where(file => file.Source.Kind == ExportContentKind.Audio && changed.ContainsKey(file.Source.EntityId) &&
            current[file.Source.EntityId].Contains(file.Path, comparer)).Select(file => file.Path).ToImmutableHashSet(comparer);
        var projection = editedPlan.Entries.Select(entry => new ReviewedDeviceExportEntry(entry, digests.GetValueOrDefault(entry.EntryId)));
        return new HostOutcome<LayoutCorrection>.Success(new(inputs with { Plan = editedPlan, PlaylistDigests = digests.ToImmutable() },
            [.. projection], retire, []));
    }

    private static HostOutcome<LayoutCorrection> Blocked(DeviceExportPreviewSources inputs, ImmutableArray<DeviceExportPreviewIssue> issues,
        StringComparer comparer) => new HostOutcome<LayoutCorrection>.Success(new(inputs, [], ImmutableHashSet.Create<string>(comparer), issues));

    /// <summary>Checks that another planned file does not occupy a directory needed by this path.</summary>
    internal static bool HasFileParent(string path, Dictionary<string, ExportContentSource> files)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            if (files.ContainsKey(path[..slash]))
                return true;
        }
        return false;
    }

    /// <summary>Enumerates the staged destinations and the complete playlist references.</summary>
    internal static IEnumerable<(ExportContentSource Source, string Path)> Paths(DeviceExportPlan plan)
    {
        foreach (var entry in plan.Entries)
        {
            if (entry is ExportEntry.Audio audio)
                yield return (new(ExportContentKind.Audio, audio.TrackId), audio.Destination);
            else if (entry is ExportEntry.Playlist playlist)
            {
                yield return (new(ExportContentKind.Playlist, playlist.PlaylistId), playlist.Destination);
                foreach (var track in playlist.Tracks)
                    yield return (new(ExportContentKind.Audio, track.TrackId), track.Destination);
            }
        }
    }

    internal sealed record LayoutCorrection(DeviceExportPreviewSources Inputs, ImmutableArray<ReviewedDeviceExportEntry> Entries,
        ImmutableHashSet<string> RetirePaths, ImmutableArray<DeviceExportPreviewIssue> Issues);
}
