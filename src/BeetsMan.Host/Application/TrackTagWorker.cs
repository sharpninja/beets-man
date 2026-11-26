using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a durable track patch and its native write checkpoint.</summary>
/// <remarks>2026-11-24: Fence existing-track tag writes for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record TrackTagWork(ImportJobSnapshot Job, Guid TrackId, long TrackRevision, long BeetsTrackId, string RelativePath,
    ImmutableDictionary<string, string> Baseline, ImmutableDictionary<string, string?> Changes, bool NativeAttempted);

/// <summary>Reports the selected item's beets database tags and independently observed embedded tags.</summary>
internal sealed record NativeTrackTags(ImmutableDictionary<string, string> Catalog, ImmutableDictionary<string, string> Embedded);

/// <summary>Writes and reads tags for the exact native item and admitted path.</summary>
internal interface IBeetsTrackTags
{
    Task<HostOutcome<NativeTrackTags>> ObserveAsync(TrackTagWork work, CancellationToken token);
    Task<HostOutcome<bool>> ApplyAsync(TrackTagWork work, CancellationToken token);
}

/// <summary>Owns the durable execution fence and atomic tag publication.</summary>
internal interface ITrackTagWorkStore
{
    Task<HostOutcome<TrackTagWork?>> ReadNextTrackTagEditAsync(Guid libraryId, CancellationToken token);
    Task<HostOutcome<TrackTagWork?>> BeginTrackTagWriteAsync(TrackTagWork work, CancellationToken token);
    Task<HostOutcome<bool>> FailTrackTagEditAsync(TrackTagWork work, string code, bool retryable, bool knownNotStarted, CancellationToken token);
    Task<HostOutcome<bool>> PublishTrackTagEditAsync(TrackTagWork work, CancellationToken token);
}

/// <summary>Applies a track patch only once and publishes the verified result on the host's serial beets lane.</summary>
/// <remarks>2026-11-24: Reconcile both native stores after an interrupted write for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal sealed class TrackTagWorker(Guid libraryId, ITrackTagWorkStore store, IBeetsTrackTags native)
{
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var selected = await store.ReadNextTrackTagEditAsync(libraryId, token).ConfigureAwait(false);
        if (selected is not HostOutcome<TrackTagWork?>.Success { Value: var work })
            return selected.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);

        var observation = await native.ObserveAsync(work, token).ConfigureAwait(false);
        if (observation is not HostOutcome<NativeTrackTags>.Success { Value: var before })
            return await UnavailableAsync(work, observation, token).ConfigureAwait(false);
        if (Matches(before, work.Changes))
            return await store.PublishTrackTagEditAsync(work, token).ConfigureAwait(false);
        if (work.NativeAttempted)
            return await store.FailTrackTagEditAsync(work, "track-tag-reconciliation-incomplete", true, false, token).ConfigureAwait(false);
        var baseline = work.Changes.Keys.ToImmutableDictionary(key => key, key => work.Baseline.GetValueOrDefault(key), StringComparer.Ordinal);
        if (!Matches(before, baseline))
            return await store.FailTrackTagEditAsync(work, "track-tag-native-baseline-changed", false, false, token).ConfigureAwait(false);

        var begun = await store.BeginTrackTagWriteAsync(work, token).ConfigureAwait(false);
        if (begun is not HostOutcome<TrackTagWork?>.Success { Value: var fenced })
            return begun.PropagateFailure<bool>();
        if (fenced is null)
            return new HostOutcome<bool>.Success(true);
        var applied = await native.ApplyAsync(fenced, token).ConfigureAwait(false);
        if (applied is HostOutcome<bool>.Success { Value: false })
            return await store.FailTrackTagEditAsync(fenced, "track-tag-write-not-started", true, true, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        observation = await native.ObserveAsync(fenced, token).ConfigureAwait(false);
        if (observation is not HostOutcome<NativeTrackTags>.Success { Value: var after })
            return await UnavailableAsync(fenced, observation, token).ConfigureAwait(false);
        return Matches(after, fenced.Changes)
            ? await store.PublishTrackTagEditAsync(fenced, token).ConfigureAwait(false)
            : await store.FailTrackTagEditAsync(fenced, "track-tag-reconciliation-incomplete", true, false, token).ConfigureAwait(false);
    }

    private Task<HostOutcome<bool>> UnavailableAsync(TrackTagWork work, HostOutcome<NativeTrackTags> observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var code = observation is HostOutcome<NativeTrackTags>.Unavailable unavailable ? unavailable.Code : "track-tag-readback-unavailable";
        return store.FailTrackTagEditAsync(work, code, true, false, token);
    }

    /// <summary>Verifies both copies of the selected native tag fields for FR-BEETS-001#ac-recover.</summary>
    internal static bool Matches(NativeTrackTags observed, ImmutableDictionary<string, string?> expected) =>
        expected.All(pair => NullIfEmpty(pair.Value) == observed.Catalog.GetValueOrDefault(pair.Key) &&
            NullIfEmpty(pair.Value) == observed.Embedded.GetValueOrDefault(pair.Key));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
