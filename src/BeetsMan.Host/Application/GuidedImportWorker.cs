using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies one durable import and, after approval, its fenced native operation.</summary>
/// <remarks>2026-11-24: Carry the selected album and recorded execution intent for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record GuidedImportWork(ImportJobSnapshot Job, ImportUpload Upload, Guid? ChoiceOperationId,
    ImportChoiceKind? Choice, string? ProviderKey, bool NativeAttempted, ApprovedBeetsMatch? ApprovedMatch = null,
    PreparedImportAlbum? PreparedMatch = null);

/// <summary>Identifies a tagged album copy verified in private before beets may write to the live library.</summary>
/// <remarks>2026-11-24: Store the verified matched-file manifest with the native attempt checkpoint for FR-BEETS-001#ac-recover.</remarks>
internal sealed record PreparedImportAlbum(string Directory, ImmutableArray<PreparedImportTrack> Tracks);

/// <summary>Contains the relative path and exact bytes of a verified tagged copy.</summary>
internal sealed record PreparedImportTrack(string RelativePath, long Size, string Sha256);

/// <summary>Accesses pending jobs and records the execution checkpoint or a recoverable failure.</summary>
internal interface IGuidedImportWorkStore
{
    Task<HostOutcome<GuidedImportWork?>> ReadNextImportAsync(Guid libraryId, CancellationToken token);
    Task<HostOutcome<bool>> RecordNativeAttemptAsync(GuidedImportWork work, PreparedImportAlbum? prepared, CancellationToken token);
    Task<HostOutcome<bool>> FailImportAsync(GuidedImportWork work, string code, bool knownNotStarted, CancellationToken token);
    Task<HostOutcome<RetryImportReceipt>> RetryImportAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        RetryImportRequest request, CancellationToken token);
}

/// <summary>Represents the actual native catalog and file observation for one approved album.</summary>
internal abstract record NativeImportObservation
{
    public sealed record Absent : NativeImportObservation;
    public sealed record Incomplete : NativeImportObservation;
    public sealed record Complete(ReconciledImportAlbum Album) : NativeImportObservation;
}

/// <summary>Looks up beets matches, applies an approved album and reconciles its durable effects.</summary>
internal interface IBeetsGuidedImport
{
    Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token);
    Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token);

    /// <summary>Prepares and verifies the approved tags in private. Keep tags can use the original staged copies.</summary>
    Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token);

    /// <summary>Revalidates a prepared album when batch preflight resumes from its saved manifest.</summary>
    /// <remarks>2026-11-24: Recover private preparation before any batch writes for FR-LIBRARY-002#ac-batch-commit.</remarks>
    Task<bool> VerifyPreparationAsync(PreparedImportAlbum prepared, CancellationToken token) => Task.FromResult(false);

    /// <summary>Returns false only when the adapter can prove the external command did not start.</summary>
    Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token);
}

/// <summary>Reconciles a recorded operation before retrying any native work.</summary>
/// <remarks>2026-11-24: Serialize review, beets execution and confirmed publication for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed class GuidedImportWorker(Guid libraryId, IGuidedImportWorkStore store, HostImportReview review,
    ImportCatalogPublication publication, IBeetsGuidedImport native)
{
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Processes one pending job on the host's serial beets lane.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        await lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var selected = await store.ReadNextImportAsync(libraryId, token).ConfigureAwait(false);
            if (selected is not HostOutcome<GuidedImportWork?>.Success { Value: var work })
                return selected.PropagateFailure<bool>();
            if (work is null)
                return new HostOutcome<bool>.Success(false);
            return work.Job.Phase == ImportJobPhase.Accepted
                ? await DiscoverAsync(work, token).ConfigureAwait(false)
                : await ApplyOrRecoverAsync(work, token).ConfigureAwait(false);
        }
        finally { lane.Release(); }
    }

    private async Task<HostOutcome<bool>> DiscoverAsync(GuidedImportWork work, CancellationToken token)
    {
        var discovered = await native.DiscoverAsync(work.Upload, token).ConfigureAwait(false);
        if (discovered is not HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success { Value: var candidates })
            return await RecordFailureAsync(work, discovered, "import-review-unavailable", token).ConfigureAwait(false);
        var offered = await review.OfferAsync(libraryId, work.Job.JobId, work.Upload.UploadId, candidates, token).ConfigureAwait(false);
        return offered is HostOutcome<ImportJobSnapshot>.Success ? new HostOutcome<bool>.Success(true) :
            await RecordFailureAsync(work, offered, "import-review-unavailable", token).ConfigureAwait(false);
    }

    private async Task<HostOutcome<bool>> ApplyOrRecoverAsync(GuidedImportWork work, CancellationToken token)
    {
        var before = await native.ObserveAsync(work, token).ConfigureAwait(false);
        if (work.NativeAttempted || before is not HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Absent })
            return await PublishObservedAsync(work, before, token).ConfigureAwait(false);

        var preparation = await native.PrepareAsync(work, token).ConfigureAwait(false);
        if (preparation is not HostOutcome<PreparedImportAlbum?>.Success { Value: var prepared })
            return await RecordFailureAsync(work, preparation, "beets-import-preparation-unavailable", token).ConfigureAwait(false);
        var saved = await store.RecordNativeAttemptAsync(work, prepared, token).ConfigureAwait(false);
        if (saved is not HostOutcome<bool>.Success { Value: true })
            return saved;
        work = work with { NativeAttempted = true, PreparedMatch = prepared };
        var applied = await native.ApplyAsync(work, token).ConfigureAwait(false);
        if (applied is HostOutcome<bool>.Success { Value: false })
            return await store.FailImportAsync(work, "beets-import-not-started", knownNotStarted: true, token).ConfigureAwait(false);
        if (applied is HostOutcome<bool>.Rejected preflight)
            return await store.FailImportAsync(work, preflight.Code, knownNotStarted: true, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // Exit status alone cannot establish whether the catalog or files were changed.
        var after = await native.ObserveAsync(work, token).ConfigureAwait(false);
        return await PublishObservedAsync(work, after, token).ConfigureAwait(false);
    }

    private async Task<HostOutcome<bool>> PublishObservedAsync(GuidedImportWork work, HostOutcome<NativeImportObservation> observed,
        CancellationToken token)
    {
        if (observed is not HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Complete complete })
            return await RecordFailureAsync(work, observed, "import-reconciliation-incomplete", token).ConfigureAwait(false);
        if (complete.Album.LibraryId != libraryId || complete.Album.JobId != work.Job.JobId ||
            complete.Album.UploadId != work.Upload.UploadId || complete.Album.ChoiceOperationId != work.ChoiceOperationId ||
            complete.Album.JobRevision != work.Job.Revision)
            return await store.FailImportAsync(work, "import-publication-scope-mismatch", false, token).ConfigureAwait(false);

        var published = await publication.PublishAsync(complete.Album, token).ConfigureAwait(false);
        return published is HostOutcome<ImportJobSnapshot>.Success ? new HostOutcome<bool>.Success(true) :
            await RecordFailureAsync(work, published, "import-publication-unavailable", token).ConfigureAwait(false);
    }

    private Task<HostOutcome<bool>> RecordFailureAsync<T>(GuidedImportWork work, HostOutcome<T> result, string fallback,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var code = result switch
        {
            HostOutcome<T>.Unavailable unavailable => unavailable.Code,
            HostOutcome<T>.Rejected rejected => rejected.Code,
            _ => fallback
        };
        return store.FailImportAsync(work, code, false, token);
    }
}
