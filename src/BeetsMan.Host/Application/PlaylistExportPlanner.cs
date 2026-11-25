using System.Collections.Immutable;
using System.Text;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a track's resolved device path and current availability.</summary>
/// <remarks>2026-11-24: Define the input to the staged-playlist rule in FR-FILESYNC-001#ac-playlist-drop.</remarks>
public sealed record ResolvedPlaylistTrack(Guid TrackId, string Destination, bool PresentOnDevice, bool SourceAvailable);

/// <summary>Contains the authoritative profile resolution for one saved playlist drop.</summary>
public sealed record ResolvedPlaylistExport(Guid PlaylistId, Guid DeviceId, string ProfileId, long ProfileRevision,
    string Format, string PlaylistDestination, bool CaseSensitivePaths, ImmutableArray<ResolvedPlaylistTrack> Tracks);

/// <summary>Contains playlist bytes and a staged plan to be persisted atomically by the export owner.</summary>
public sealed record PlaylistStageMutation(DeviceExportPlan Plan, Guid PlaylistEntryId, ImmutableArray<byte> PlaylistContent);

/// <summary>Formats one registered app-specific playlist from its ordered relative track paths.</summary>
public interface IExportPlaylistFormatter
{
    /// <summary>Gets the profile format key implemented by this formatter.</summary>
    string Format { get; }

    /// <summary>Renders immutable bytes for the selected profile.</summary>
    ImmutableArray<byte> FormatTracks(ImmutableArray<string> orderedRelativeTracks);
}

/// <summary>Formats relative-path UTF-8 M3U8 for the default Android profile.</summary>
public sealed class M3u8PlaylistFormatter : IExportPlaylistFormatter
{
    /// <inheritdoc />
    public string Format => "m3u8";

    /// <inheritdoc />
    public ImmutableArray<byte> FormatTracks(ImmutableArray<string> orderedRelativeTracks) =>
        [.. Encoding.UTF8.GetBytes("#EXTM3U\n" + string.Concat(orderedRelativeTracks.Select(track => track + "\n")))];
}

/// <summary>Calculates the staged playlist and distinct missing audio without touching a device share.</summary>
/// <remarks>2026-11-24: Implement the authoritative staging calculation for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class PlaylistExportPlanner(IEnumerable<IExportPlaylistFormatter> formats)
{
    private readonly Dictionary<string, IExportPlaylistFormatter> formatters = formats.ToDictionary(item => item.Format, StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the staged mutation at the exact selected device, profile, and plan revision.</summary>
    public HostOutcome<PlaylistStageMutation> Stage(StageDevicePlaylistRequest request, ResolvedPlaylistExport source, DeviceExportPlan? existing)
    {
        if (request.PlaylistId == Guid.Empty || request.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProfileId) ||
            (request.PlanId is null) != (request.ExpectedPlanRevision is null))
            return Invalid("invalid-export-target");

        if (source.PlaylistId != request.PlaylistId || source.DeviceId != request.DeviceId ||
            source.ProfileId != request.ProfileId || source.ProfileRevision != request.ProfileRevision)
            return new HostOutcome<PlaylistStageMutation>.Rejected("export-profile-changed", source.ProfileRevision, Conflict: true);

        if (existing?.PlanId != request.PlanId || existing?.PlanRevision != request.ExpectedPlanRevision ||
            existing is not null && (existing.DeviceId != request.DeviceId || existing.ProfileId != request.ProfileId ||
                existing.ProfileRevision != request.ProfileRevision))
            return new HostOutcome<PlaylistStageMutation>.Rejected("export-plan-changed", existing?.PlanRevision, Conflict: true);

        if (string.IsNullOrWhiteSpace(source.Format) || !formatters.TryGetValue(source.Format, out var formatter))
            return Invalid("unsupported-playlist-format");
        if (!TryNormalize(source.PlaylistDestination, out var playlistPath) || source.Tracks.IsDefault)
            return Invalid("invalid-playlist-destination");

        var comparer = source.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var byDestination = new Dictionary<string, ExportEntry>(comparer);
        var directories = new HashSet<string>(comparer);
        var staged = ImmutableArray.CreateBuilder<ExportEntry>();
        if (existing is { Entries.IsDefaultOrEmpty: false })
        {
            foreach (var prior in existing.Entries)
            {
                if (!TryNormalize(prior.Destination, out var normalized) || !ReservePath(normalized, byDestination, directories) ||
                    !byDestination.TryAdd(normalized, prior))
                    return Invalid("invalid-staged-destination");
                staged.Add(prior);
            }
        }

        byDestination.TryGetValue(playlistPath, out var priorPlaylist);
        if (!ReservePath(playlistPath, byDestination, directories) ||
            priorPlaylist is not null && (priorPlaylist is not ExportEntry.Playlist playlist || playlist.PlaylistId != source.PlaylistId))
            return Invalid("playlist-destination-collision");
        var playlistEntryId = priorPlaylist?.EntryId ?? Guid.NewGuid();
        byDestination[playlistPath] = new ExportEntry.Playlist(playlistEntryId, playlistPath, source.PlaylistId, formatter.Format, []);

        var referenceTracks = ImmutableArray.CreateBuilder<ExportTrackReference>(source.Tracks.Length);
        var relativeTracks = ImmutableArray.CreateBuilder<string>(source.Tracks.Length);
        var newAudio = ImmutableArray.CreateBuilder<ExportEntry.Audio>();
        var resolvedAvailability = new Dictionary<string, bool>(comparer);
        foreach (var track in source.Tracks)
        {
            if (track.TrackId == Guid.Empty || !TryNormalize(track.Destination, out var trackPath) ||
                !ReservePath(trackPath, byDestination, directories))
                return Invalid("invalid-track-destination");

            byDestination.TryGetValue(trackPath, out var priorAudio);
            if (priorAudio is not null && (priorAudio is not ExportEntry.Audio audio || audio.TrackId != track.TrackId))
                return Invalid("audio-destination-collision");
            if (resolvedAvailability.TryGetValue(trackPath, out var priorPresent) && priorPresent != track.PresentOnDevice)
                return Invalid("inconsistent-export-inventory");
            resolvedAvailability[trackPath] = track.PresentOnDevice;

            var status = track.PresentOnDevice ? ExportAudioStatus.Present : ExportAudioStatus.Staged;
            if (!track.PresentOnDevice && priorAudio is not ExportEntry.Audio { Status: ExportAudioStatus.Staged })
            {
                if (!track.SourceAvailable)
                    return Invalid("export-source-unavailable");
                var copy = new ExportEntry.Audio(priorAudio?.EntryId ?? Guid.NewGuid(), trackPath, track.TrackId, ExportAudioStatus.Staged)
                    { IsDirect = priorAudio is ExportEntry.Audio { IsDirect: true } };
                if (priorAudio is not null)
                    staged[staged.IndexOf(priorAudio)] = copy;
                else
                    newAudio.Add(copy);
                byDestination[trackPath] = copy;
            }
            else if (priorAudio is null)
            {
                // Reserve the already-present target for collision detection without enqueuing a copy.
                byDestination.Add(trackPath, new ExportEntry.Audio(Guid.Empty, trackPath, track.TrackId, ExportAudioStatus.Present));
            }

            referenceTracks.Add(new(track.TrackId, trackPath, status));
            relativeTracks.Add(RelativeToPlaylist(playlistPath, trackPath, comparer));
        }

        var playlistEntry = new ExportEntry.Playlist(playlistEntryId, playlistPath, source.PlaylistId,
            formatter.Format, referenceTracks.MoveToImmutable());
        if (priorPlaylist is null)
            staged.Add(playlistEntry);
        else
            staged[staged.IndexOf(priorPlaylist)] = playlistEntry;
        staged.AddRange(newAudio);
        var plan = new DeviceExportPlan(existing?.PlanId ?? Guid.NewGuid(), checked((existing?.PlanRevision ?? 0) + 1),
            source.DeviceId, source.ProfileId, source.ProfileRevision, staged.ToImmutable());
        return new HostOutcome<PlaylistStageMutation>.Success(new(plan, playlistEntry.EntryId, formatter.FormatTracks(relativeTracks.MoveToImmutable())));
    }

    /// <summary>Validates and normalizes a share-relative destination before looking it up or staging it.</summary>
    /// <remarks>2026-11-24: Share exact receiver lookup paths with the stage planner for FR-FILESYNC-001#ac-safety.</remarks>
    internal static bool TryNormalize(string? original, out string normalized)
    {
        normalized = original?.Replace('\\', '/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var segments = normalized.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not ".." &&
            !segment.EndsWith(' ') && !segment.EndsWith('.') &&
            !segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)) && !IsReserved(segment));
    }

    /// <summary>Checks that the candidate is a file path with no file ancestor in the same staged plan.</summary>
    /// <remarks>2026-11-24: Share collision checks with direct track staging for FR-FILESYNC-001#ac-safety.</remarks>
    internal static bool ReservePath(string relative, Dictionary<string, ExportEntry> files, HashSet<string> directories)
    {
        if (directories.Contains(relative))
            return false;
        for (int index = relative.IndexOf('/'); index >= 0; index = relative.IndexOf('/', index + 1))
        {
            var parent = relative[..index];
            if (files.ContainsKey(parent))
                return false;
            directories.Add(parent);
        }
        return true;
    }

    private static bool IsReserved(string segment)
    {
        if (segment.StartsWith(".syncthing.", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("~syncthing~", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".stfolder", StringComparison.OrdinalIgnoreCase) || segment.Equals(".stignore", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".stversions", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = segment.Split('.', 2)[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    /// <summary>Renders a safe relative track reference from its playlist location.</summary>
    internal static string RelativeToPlaylist(string playlist, string track, StringComparer comparer)
    {
        var from = playlist.Split('/');
        var to = track.Split('/');
        var common = 0;
        while (common < from.Length - 1 && common < to.Length - 1 && comparer.Equals(from[common], to[common]))
            common++;
        var relative = string.Join('/', Enumerable.Repeat("..", from.Length - 1 - common).Concat(to.Skip(common)));
        // A bare leading # is metadata in a line-oriented playlist format.
        return relative.StartsWith('#') ? "./" + relative : relative;
    }

    private static HostOutcome<PlaylistStageMutation> Invalid(string code) => new HostOutcome<PlaylistStageMutation>.Rejected(code);
}
