using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Holds the verified upload, explicit decision, and durable native evidence for one batch entry.</summary>
/// <remarks>2026-11-24: Scope batch imports to their approved entry and choice for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal sealed record NativeBatchImport(ImportUpload Upload, ApprovedBatchImportChoiceReceipt Decision, string? Provider,
    ApprovedBeetsMatch? Match, PreparedImportAlbum? Prepared, ReconciledImportAlbum? Observed)
{
    /// <summary>Builds the scoped native operation without creating an independent library job.</summary>
    internal GuidedImportWork NativeWork(bool attempted) => new(new(Upload.UploadId, Upload.LibraryId, Decision.Revision, ImportJobPhase.Running),
        Upload, Decision.OperationId, Decision.Choice, Provider, attempted, Match, Prepared);

    /// <summary>Checks that the verified album accounts for every file ordinal in this operation.</summary>
    internal ReconciledImportAlbum? Verify(ReconciledImportAlbum album) => album.LibraryId == Upload.LibraryId && album.JobId == Upload.UploadId &&
        album.UploadId == Upload.UploadId && album.ChoiceOperationId == Decision.OperationId && album.JobRevision == Decision.Revision &&
        album.ProviderKey == Provider && album.Tracks.Length == Upload.Files.Length ? ImportCatalogPublication.Normalize(album) : null;
}

internal sealed partial class NativeBatchWorker
{
    /// <summary>Verifies all non-skipped inputs and prepares matched copies before the batch makes native edits.</summary>
    private async Task<HostOutcome<bool>> PrepareImportsAsync(NativeBatchWork work, CancellationToken token)
    {
        foreach (var entry in work.Entries)
        {
            if (entry.Import is not { } import || import.Decision.Choice == ImportChoiceKind.Skip)
                continue;
            var verified = await stages(work.Status.OperationId).VerifyAsync(import.Upload, token).ConfigureAwait(false);
            if (verified is not HostOutcome<bool>.Success { Value: true })
                return new HostOutcome<bool>.Unavailable("batch-import-copies-unavailable");
            var native = imports(work.Status.OperationId);
            var input = import.NativeWork(false);
            if (await native.ObserveAsync(input, token).ConfigureAwait(false) is not
                HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Absent })
                return new HostOutcome<bool>.Unavailable("batch-import-baseline-unavailable");
            if (import.Decision.Choice == ImportChoiceKind.KeepTags)
                continue;
            if (import.Prepared is { } saved)
            {
                if (!await native.VerifyPreparationAsync(saved, token).ConfigureAwait(false))
                    return new HostOutcome<bool>.Unavailable("batch-import-preparation-unavailable");
                continue;
            }
            var prepared = await native.PrepareAsync(input, token).ConfigureAwait(false);
            if (prepared is not HostOutcome<PreparedImportAlbum?>.Success { Value: var manifest })
                return prepared.PropagateFailure<bool>();
            if (manifest?.Tracks.Length != import.Upload.Files.Length)
                return new HostOutcome<bool>.Unavailable("batch-import-preparation-unavailable");
            var recorded = await store.SaveNativeBatchImportPreparationAsync(work, import, manifest, token).ConfigureAwait(false);
            if (recorded is not HostOutcome<bool>.Success { Value: true })
                return recorded;
        }
        return new HostOutcome<bool>.Success(true);
    }

    /// <summary>Fences the exact native command and reconciles the operation after an interrupted application.</summary>
    private async Task<HostOutcome<bool>> RunImportAsync(NativeBatchWork work, NativeBatchImport import, CancellationToken token)
    {
        if (import.Decision.Choice == ImportChoiceKind.Skip)
            return await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false);

        var native = imports(work.Status.OperationId);
        var input = import.NativeWork(work.EntryAttempted);
        var before = await native.ObserveAsync(input, token).ConfigureAwait(false);
        if (work.EntryAttempted)
            return await RecordImportAsync(work, import, before, token).ConfigureAwait(false);
        if (before is not HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Absent })
            return new HostOutcome<bool>.Unavailable("batch-import-baseline-unavailable");

        var fence = await store.MarkNativeBatchEntryAttemptedAsync(work, token).ConfigureAwait(false);
        if (fence is not HostOutcome<bool>.Success { Value: true })
            return fence;
        input = input with { NativeAttempted = true };
        work = work with { EntryAttempted = true };
        var applied = await native.ApplyAsync(input, token).ConfigureAwait(false);
        if (applied is HostOutcome<bool>.Success { Value: false } or HostOutcome<bool>.Rejected)
        {
            var reset = await store.ResetNativeBatchImportAttemptAsync(work, token).ConfigureAwait(false);
            return reset is HostOutcome<bool>.Success ? new HostOutcome<bool>.Unavailable("batch-import-not-started") : reset;
        }
        token.ThrowIfCancellationRequested();
        var after = await native.ObserveAsync(input, token).ConfigureAwait(false);
        return await RecordImportAsync(work, import, after, token).ConfigureAwait(false);
    }

    /// <summary>Commits native evidence to the journal while leaving catalog publication pending.</summary>
    private Task<HostOutcome<bool>> RecordImportAsync(NativeBatchWork work, NativeBatchImport import, HostOutcome<NativeImportObservation> observed,
        CancellationToken token) => observed is HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Complete complete } &&
        import.Verify(complete.Album) is { } verified ? store.RecordNativeBatchImportAsync(work, verified, token) :
        Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Unavailable("batch-import-reconciliation-incomplete"));

    /// <summary>Rechecks verified copies and their native album and track identities before publishing.</summary>
    private async Task<HostOutcome<bool>> VerifyImportsAsync(NativeBatchWork work, CancellationToken token)
    {
        foreach (var entry in work.Entries)
        {
            if (entry.Import is not { Observed: { } expected } import)
                continue;
            var observed = await imports(work.Status.OperationId).ObserveAsync(import.NativeWork(true), token).ConfigureAwait(false);
            if (observed is not HostOutcome<NativeImportObservation>.Success { Value: NativeImportObservation.Complete complete } ||
                import.Verify(complete.Album) is not { } verified || expected with { Tracks = verified.Tracks } != verified ||
                !expected.Tracks.SequenceEqual(verified.Tracks))
                return new HostOutcome<bool>.Unavailable("batch-import-reconciliation-incomplete");
        }
        return new HostOutcome<bool>.Success(true);
    }
}
