using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Pins one received file to its approved batch receipt and ordered private upload manifest.</summary>
/// <remarks>2026-11-24: Check exact approved copies for FR-LIBRARY-002#ac-batch-commit.</remarks>
internal sealed record ApprovedBatchImportTarget(ApprovedBatchImportPartReceipt Receipt, ImportUpload Upload);

/// <summary>Reads the authenticated transfer scope and records independently verified copies.</summary>
internal interface IApprovedBatchImportStore
{
    Task<HostOutcome<ApprovedBatchImportTarget>> ReadBatchImportPartAsync(Guid library, AuthenticatedHostInstance caller, Guid approval,
        int position, int index, Guid file, CancellationToken token);
    Task<HostOutcome<ApprovedBatchImportPartReceipt>> RecordBatchImportPartAsync(AuthenticatedHostInstance caller, ApprovedBatchImportPartReceipt receipt,
        CancellationToken token);
}

/// <summary>Transfers the exact reviewed bytes into the private staging area for one approved batch.</summary>
/// <remarks>2026-11-24: Keep approved import transfer separate from publication for FR-LIBRARY-002#ac-batch-local and ac-batch-commit.</remarks>
internal sealed class HostApprovedBatchImports(IApprovedBatchImportStore store, Func<Guid, IImportFileStage> stage)
{
    /// <summary>Checks the manifest, verifies the received bytes, and only then records the receipt.</summary>
    public async Task<HostOutcome<ApprovedBatchImportPartReceipt>> PutAsync(Guid library, AuthenticatedHostInstance caller, Guid approval,
        int position, int index, Guid file, long? contentLength, Stream source, Action<long> limitBody, CancellationToken token)
    {
        if (approval == Guid.Empty || position < 1 || index < 0 || file == Guid.Empty)
            return new HostOutcome<ApprovedBatchImportPartReceipt>.Rejected("invalid-batch-import-part");
        var selected = await store.ReadBatchImportPartAsync(library, caller, approval, position, index, file, token).ConfigureAwait(false);
        if (selected is not HostOutcome<ApprovedBatchImportTarget>.Success { Value: var target })
            return selected.PropagateFailure<ApprovedBatchImportPartReceipt>();
        var manifest = target.Upload.Files[index];
        if (contentLength != manifest.Size)
            return new HostOutcome<ApprovedBatchImportPartReceipt>.Rejected("invalid-batch-import-size");
        limitBody(manifest.Size);
        var verified = await stage(approval).CopyAsync(target.Upload, index, source, token).ConfigureAwait(false);
        if (verified is not HostOutcome<ImportUploadPartReceipt>.Success { Value: var copy })
            return verified.PropagateFailure<ApprovedBatchImportPartReceipt>();
        if (copy.UploadId != target.Upload.UploadId || copy.Index != index)
            return new HostOutcome<ApprovedBatchImportPartReceipt>.Unavailable("batch-import-copy-mismatch");
        return await store.RecordBatchImportPartAsync(caller, target.Receipt, token).ConfigureAwait(false);
    }
}
