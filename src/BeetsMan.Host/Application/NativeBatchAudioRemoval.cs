using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Preserves the reviewed file deletion path across earlier approved batch effects.</summary>
/// <remarks>2026-11-24: Execute exact audio deletion in a batch for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal sealed partial class NativeBatchWorker
{
    /// <summary>Uses the updated native catalog path and the independently approved file path.</summary>
    private async Task<HostOutcome<bool>> RunAudioRemovalAsync(NativeBatchWork work, NativeBatchEntry entry, CancellationToken token)
    {
        var paths = CompletedOrganizationPaths(work);
        var files = entry.Targets.Zip(entry.AudioFiles).Select(pair => ReviewedAudioAfterTags(work, pair.First.TrackId, pair.Second)).ToImmutableArray();
        var before = await AudioRemoval.ReadPendingAsync(entry.Targets, files, work.EntryAttempted, catalog, audio, token, paths).ConfigureAwait(false);
        if (before is not HostOutcome<ImmutableArray<AudioRemoval.PendingEffect>>.Success { Value: var remaining })
            return before.PropagateFailure<bool>();
        if (!work.EntryAttempted)
            return await store.MarkNativeBatchEntryAttemptedAsync(work, token).ConfigureAwait(false);
        var applied = await AudioRemoval.ApplyAsync(remaining, catalog, audio, token).ConfigureAwait(false);
        if (applied is not HostOutcome<bool>.Success { Value: true })
            return applied;
        var after = await AudioRemoval.ReadPendingAsync(entry.Targets, files, true, catalog, audio, token, paths).ConfigureAwait(false);
        return after is HostOutcome<ImmutableArray<AudioRemoval.PendingEffect>>.Success { Value.IsEmpty: true }
            ? await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false)
            : new HostOutcome<bool>.Unavailable("audio-removal-reconciliation-incomplete");
    }

    /// <summary>Verifies every separately approved file is absent before the peer revision is committed.</summary>
    private async Task<HostOutcome<HashSet<string>>> VerifyDeletedAudioAsync(NativeBatchWork work, CancellationToken token)
    {
        var deleted = new HashSet<string>(ImportCatalogPublication.PathComparer);
        foreach (var file in work.Entries.Where(entry => entry.DeletesAudio).SelectMany(entry => entry.AudioFiles))
        {
            if (await audio.ObserveAudioFileAsync(file, token).ConfigureAwait(false) is not
                HostOutcome<NativeAudioFileState>.Success { Value: NativeAudioFileState.Missing })
                return new HostOutcome<HashSet<string>>.Unavailable("audio-removal-reconciliation-incomplete");
            deleted.Add(file.RelativePath);
        }
        return new HostOutcome<HashSet<string>>.Success(deleted);
    }

    /// <summary>Finds a tag input's last verified fingerprint at its current path.</summary>
    private static OrganizationFileStamp? ExpectedTagInput(NativeBatchWork work, Guid track)
    {
        foreach (var entry in work.Entries.Take(work.NextPosition - 1).Reverse())
        {
            if (entry.Approved.Change is ApprovedBatchChange.TrackTags edit && edit.TrackId == track && entry.TaggedFile is { } stamp)
                return stamp;
            if (!entry.OrganizationCopies.IsDefaultOrEmpty && entry.OrganizationCopies.FirstOrDefault(copy => copy.Track.TrackId == track) is { } copy)
                return new(copy.Size, copy.Sha256);
        }
        foreach (var entry in work.Entries.Where(entry => entry.DeletesAudio))
            foreach (var (target, file) in entry.Targets.Zip(entry.AudioFiles))
                if (target.TrackId == track)
                    return new(file.Size, file.Sha256);
        return null;
    }

    /// <summary>Updates the approved file fingerprint only from completed tag effects at the reviewed path.</summary>
    private static AudioRemovalFile ReviewedAudioAfterTags(NativeBatchWork work, Guid track, AudioRemovalFile reviewed)
    {
        var current = reviewed;
        foreach (var entry in work.Entries.Take(work.NextPosition - 1))
        {
            if (entry.Moves.Any(move => move.TrackId == track))
                break;
            if (entry.Approved.Change is ApprovedBatchChange.TrackTags edit && edit.TrackId == track && entry.TaggedFile is { } stamp)
                current = current with { Size = stamp.Size, Sha256 = stamp.Sha256 };
        }
        return current;
    }
}
