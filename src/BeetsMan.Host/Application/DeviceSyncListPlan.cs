using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Builds the next staged-list revision without changing published files.</summary>
/// <remarks>2026-11-24: Keep the STB10 explicit selections and playlist dependencies for FR-FILESYNC-001#ac-safety.</remarks>
internal static class DeviceSyncListPlan
{
    /// <summary>Appends new distinct tracks and makes reused audio an explicit selection.</summary>
    internal static HostOutcome<DeviceExportPlan> Stage(StageDeviceTracksRequest request, DeviceExportPlan? prior,
        bool caseSensitive, ImmutableArray<ResolvedPlaylistTrack> tracks)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var destinations = new Dictionary<string, ExportEntry>(comparer);
        var directories = new HashSet<string>(comparer);
        var entries = (prior?.Entries ?? []).ToBuilder();
        foreach (var entry in entries)
        {
            if (!PlaylistExportPlanner.TryNormalize(entry.Destination, out var path) ||
                !PlaylistExportPlanner.ReservePath(path, destinations, directories) || !destinations.TryAdd(path, entry))
                return Reject("invalid-staged-destination");
        }

        // Already-present audio can be referenced by an existing playlist without its own row.
        foreach (var reference in entries.OfType<ExportEntry.Playlist>().SelectMany(playlist => playlist.Tracks))
        {
            if (!PlaylistExportPlanner.TryNormalize(reference.Destination, out var path) ||
                !PlaylistExportPlanner.ReservePath(path, destinations, directories))
                return Reject("invalid-staged-destination");
            if (!destinations.TryAdd(path, new ExportEntry.Audio(Guid.Empty, path, reference.TrackId, reference.Status)) &&
                (destinations[path] is not ExportEntry.Audio audio || audio.TrackId != reference.TrackId))
                return Reject("audio-destination-collision");
        }

        foreach (var track in tracks)
        {
            if (!PlaylistExportPlanner.TryNormalize(track.Destination, out var path) ||
                !PlaylistExportPlanner.ReservePath(path, destinations, directories))
                return Reject("invalid-track-destination");
            destinations.TryGetValue(path, out var previous);
            if (previous is not null && (previous is not ExportEntry.Audio audio || audio.TrackId != track.TrackId))
                return Reject("audio-destination-collision");
            if (!track.PresentOnDevice && !track.SourceAvailable)
                return Reject("export-source-unavailable");

            var existing = previous as ExportEntry.Audio;
            var status = !track.PresentOnDevice || existing?.Status == ExportAudioStatus.Staged
                ? ExportAudioStatus.Staged : ExportAudioStatus.Present;
            var next = new ExportEntry.Audio(existing is { EntryId: var id } && id != Guid.Empty ? id : Guid.NewGuid(),
                existing?.Destination ?? path, track.TrackId, status) { IsDirect = true };
            if (existing is null || existing.EntryId == Guid.Empty)
                entries.Add(next);
            else
                entries[entries.IndexOf(existing)] = next;
            destinations[path] = next;
        }

        return new HostOutcome<DeviceExportPlan>.Success(new(prior?.PlanId ?? Guid.NewGuid(),
            checked((prior?.PlanRevision ?? 0) + 1), request.DeviceId, request.ProfileId, request.ProfileRevision, entries.ToImmutable()));
    }

    /// <summary>Removes chosen intents and retains staged audio still required by any remaining playlist.</summary>
    internal static HostOutcome<DeviceExportPlan> Remove(DeviceExportPlan plan, ImmutableArray<Guid> ids, bool caseSensitive)
    {
        var selected = ids.ToHashSet();
        if (!selected.IsSubsetOf(plan.Entries.Select(entry => entry.EntryId)))
            return Reject("export-entry-unavailable");

        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var needed = plan.Entries.OfType<ExportEntry.Playlist>().Where(playlist => !selected.Contains(playlist.EntryId))
            .SelectMany(playlist => playlist.Tracks).ToLookup(track => track.Destination, comparer);
        var retained = ImmutableArray.CreateBuilder<ExportEntry>();
        foreach (var entry in plan.Entries)
        {
            var removed = selected.Contains(entry.EntryId);
            if (entry is ExportEntry.Audio audio)
            {
                if (audio.IsDirect && !removed)
                    retained.Add(audio);
                else if (audio.Status == ExportAudioStatus.Staged && needed[audio.Destination].Any(track => track.TrackId == audio.TrackId))
                    retained.Add(audio with { IsDirect = false });
            }
            else if (!removed)
                retained.Add(entry);
        }
        return new HostOutcome<DeviceExportPlan>.Success(plan with { PlanRevision = checked(plan.PlanRevision + 1), Entries = retained.ToImmutable() });
    }

    private static HostOutcome<DeviceExportPlan> Reject(string code) => new HostOutcome<DeviceExportPlan>.Rejected(code);
}
