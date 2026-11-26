using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies the admitted removal and its ordered native targets.</summary>
/// <remarks>2026-11-24: Carry the durable native checkpoint for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record CatalogRemovalWork(ImportJobSnapshot Job, LibraryMaintenancePlan Plan, ImmutableArray<MaintenanceTrack> Tracks, bool NativeAttempted,
    ImmutableArray<AudioRemovalFile> AudioFiles = default);

/// <summary>Distinguishes a matching item, a confirmed absence, and an identity with a changed path.</summary>
internal enum NativeCatalogTrackState { Present, Missing, Diverged }

/// <summary>Reads and removes selected records from the native catalog.</summary>
internal interface IBeetsCatalogRemoval
{
    /// <summary>Reads one native identity and checks that it still matches the approved path.</summary>
    Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token);

    /// <summary>Removes one exact native record while retaining the media file.</summary>
    Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token);
}

/// <summary>Owns the native execution checkpoint and publication transaction for catalog removal with or without explicit file deletion.</summary>
internal interface ICatalogRemovalWorkStore
{
    Task<HostOutcome<CatalogRemovalWork?>> ReadNextCatalogRemovalAsync(Guid libraryId, CancellationToken token);
    Task<HostOutcome<CatalogRemovalWork?>> PrepareCatalogRemovalAsync(CatalogRemovalWork work, CancellationToken token);
    Task<HostOutcome<bool>> FailCatalogRemovalAsync(CatalogRemovalWork work, string code, bool retryable, CancellationToken token);
    Task<HostOutcome<bool>> PublishCatalogRemovalAsync(CatalogRemovalWork work, CancellationToken token);
}

/// <summary>Reconciles the approved removal on the serialized native library worker before publishing.</summary>
/// <remarks>2026-11-24: Resume partial removals and publish only after complete readback for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
/// <remarks>2026-11-24: Separate and verify audio deletion for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed class CatalogRemovalWorker(Guid libraryId, ICatalogRemovalWorkStore store, IBeetsCatalogRemoval native, IAudioRemovalFiles audio)
{
    /// <summary>Recovers or executes the next accepted removal at its exact approved mode.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var selected = await store.ReadNextCatalogRemovalAsync(libraryId, token).ConfigureAwait(false);
        if (selected is not HostOutcome<CatalogRemovalWork?>.Success { Value: var work })
            return selected.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);
        if (work.Plan.Kind == LibraryMaintenanceKind.RemoveAudio)
            return await RunAudioRemovalAsync(work, token).ConfigureAwait(false);

        var observation = await ReadRemainingAsync(work, token).ConfigureAwait(false);
        if (observation is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var remaining })
            return await SaveObservationFailureAsync(work, observation, token).ConfigureAwait(false);
        if (!work.NativeAttempted && remaining.Length != work.Tracks.Length)
            return await store.FailCatalogRemovalAsync(work, "maintenance-native-track-changed", false, token).ConfigureAwait(false);

        var prepared = await store.PrepareCatalogRemovalAsync(work, token).ConfigureAwait(false);
        if (prepared is not HostOutcome<CatalogRemovalWork?>.Success { Value: var fenced })
            return prepared.PropagateFailure<bool>();
        if (fenced is null)
            return new HostOutcome<bool>.Success(true);
        if (remaining.IsEmpty)
            return await store.PublishCatalogRemovalAsync(fenced, token).ConfigureAwait(false);

        foreach (var track in remaining)
        {
            var attempted = await native.RemoveCatalogTrackAsync(track, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (attempted is not HostOutcome<bool>.Success { Value: true })
                break;
        }

        observation = await ReadRemainingAsync(fenced, token).ConfigureAwait(false);
        if (observation is not HostOutcome<ImmutableArray<MaintenanceTrack>>.Success { Value: var after })
            return await SaveObservationFailureAsync(fenced, observation, token).ConfigureAwait(false);
        return after.IsEmpty ? await store.PublishCatalogRemovalAsync(fenced, token).ConfigureAwait(false) :
            await store.FailCatalogRemovalAsync(fenced, "maintenance-catalog-removal-incomplete", true, token).ConfigureAwait(false);
    }

    /// <summary>Applies the separately approved media effects and verifies the complete selection before publishing.</summary>
    private async Task<HostOutcome<bool>> RunAudioRemovalAsync(CatalogRemovalWork work, CancellationToken token)
    {
        var before = await AudioRemoval.ReadPendingAsync(work.Tracks, work.AudioFiles, work.NativeAttempted, native, audio, token).ConfigureAwait(false);
        if (before is not HostOutcome<ImmutableArray<AudioRemoval.PendingEffect>>.Success { Value: var pending })
            return await SaveAudioFailureAsync(work, before, token).ConfigureAwait(false);
        var prepared = await store.PrepareCatalogRemovalAsync(work, token).ConfigureAwait(false);
        if (prepared is not HostOutcome<CatalogRemovalWork?>.Success { Value: var fenced })
            return prepared.PropagateFailure<bool>();
        if (fenced is null)
            return new HostOutcome<bool>.Success(true);
        var processed = await AudioRemoval.ApplyAsync(pending, native, audio, token).ConfigureAwait(false);
        if (processed is not HostOutcome<bool>.Success { Value: true })
            return await SaveAudioFailureAsync(fenced, processed, token).ConfigureAwait(false);
        var after = await AudioRemoval.ReadPendingAsync(fenced.Tracks, fenced.AudioFiles, true, native, audio, token).ConfigureAwait(false);
        return after is HostOutcome<ImmutableArray<AudioRemoval.PendingEffect>>.Success { Value.IsEmpty: true }
            ? await store.PublishCatalogRemovalAsync(fenced, token).ConfigureAwait(false)
            : await SaveAudioFailureAsync(fenced, after, token).ConfigureAwait(false);
    }

    /// <summary>Records a stale preflight or a retryable interrupted file effect without publishing a tombstone.</summary>
    private Task<HostOutcome<bool>> SaveAudioFailureAsync<T>(CatalogRemovalWork work, HostOutcome<T> outcome, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return outcome switch
        {
            HostOutcome<T>.Rejected rejected => store.FailCatalogRemovalAsync(work, rejected.Code, work.NativeAttempted, token),
            HostOutcome<T>.Unavailable unavailable => store.FailCatalogRemovalAsync(work, unavailable.Code, true, token),
            _ => store.FailCatalogRemovalAsync(work, "audio-removal-reconciliation-incomplete", true, token)
        };
    }

    private async Task<HostOutcome<ImmutableArray<MaintenanceTrack>>> ReadRemainingAsync(CatalogRemovalWork work, CancellationToken token)
    {
        var remaining = ImmutableArray.CreateBuilder<MaintenanceTrack>();
        foreach (var track in work.Tracks)
        {
            var result = await native.ObserveCatalogTrackAsync(track, token).ConfigureAwait(false);
            if (result is not HostOutcome<NativeCatalogTrackState>.Success { Value: var state })
                return result.PropagateFailure<ImmutableArray<MaintenanceTrack>>();
            if (state == NativeCatalogTrackState.Diverged)
                return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected("maintenance-native-track-changed", Conflict: true);
            if (state == NativeCatalogTrackState.Present)
                remaining.Add(track);
        }
        return new HostOutcome<ImmutableArray<MaintenanceTrack>>.Success(remaining.ToImmutable());
    }

    private Task<HostOutcome<bool>> SaveObservationFailureAsync(CatalogRemovalWork work,
        HostOutcome<ImmutableArray<MaintenanceTrack>> observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return observation is HostOutcome<ImmutableArray<MaintenanceTrack>>.Rejected rejected
            ? store.FailCatalogRemovalAsync(work, rejected.Code, false, token)
            : store.FailCatalogRemovalAsync(work, "maintenance-native-catalog-unavailable", true, token);
    }
}
