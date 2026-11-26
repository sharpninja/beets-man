using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Captures the immutable copy manifest and optional accepted job for an authorized source upload.</summary>
/// <remarks>2026-11-24: Fence completed input for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record ImportUpload(Guid LibraryId, Guid OwnerId, Guid UploadId, ImmutableArray<ImportUploadFile> Files, Guid? JobId = null);

/// <summary>Accesses the authoritative manifest and accepted guided import job.</summary>
internal interface IImportIntakeStore
{
    Task<HostOutcome<ImportUploadReceipt>> BeginUploadAsync(ImportUpload upload, AuthenticatedHostInstance caller, CancellationToken token);
    Task<HostOutcome<ImportUpload>> ReadUploadAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid uploadId, CancellationToken token);
    Task<HostOutcome<StartImportReceipt?>> ReadStartReceiptAsync(Guid libraryId, AuthenticatedHostInstance caller, StartImportRequest request, CancellationToken token);
    Task<HostOutcome<StartImportReceipt>> AcceptImportAsync(ImportUpload verified, AuthenticatedHostInstance caller, StartImportRequest request, CancellationToken token);
    Task<HostOutcome<ImportJobSnapshot>> ReadImportJobAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid jobId, CancellationToken token);
}

/// <summary>Stages and verifies input copies in the host's private upload area.</summary>
internal interface IImportFileStage
{
    Task<HostOutcome<ImportUploadPartReceipt>> CopyAsync(ImportUpload upload, int index, Stream source, CancellationToken token);
    Task<HostOutcome<bool>> VerifyAsync(ImportUpload upload, CancellationToken token);
}

/// <summary>Accepts a guided import only after every selected file has a verified private copy.</summary>
/// <remarks>2026-11-24: Bind the STB08 intake to FR-BEETS-001#ac-import, ac-recover, and TR-BEETS-ARCH-001.</remarks>
internal sealed class HostImportIntake(IImportIntakeStore store, IImportFileStage staging, IGuidedImportWorkStore jobs)
{
    /// <summary>Reserves an immutable, ordered manifest at the authenticated operation ID.</summary>
    public Task<HostOutcome<ImportUploadReceipt>> BeginAsync(Guid libraryId, AuthenticatedHostInstance caller,
        BeginImportUploadRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<ImportUploadReceipt>>(new HostOutcome<ImportUploadReceipt>.Forbidden());
        if (request.UploadId == Guid.Empty || request.Files.IsDefaultOrEmpty || request.Files.Any(file => !ValidManifestFile(file)))
            return Task.FromResult<HostOutcome<ImportUploadReceipt>>(new HostOutcome<ImportUploadReceipt>.Rejected("invalid-import-manifest"));
        var files = request.Files.Select(file => file with { Sha256 = file.Sha256.ToUpperInvariant() }).ToImmutableArray();
        return store.BeginUploadAsync(new(libraryId, caller.InstanceId, request.UploadId, files), caller, token);
    }

    /// <summary>Streams only a file declared in an existing authorized upload.</summary>
    public async Task<HostOutcome<ImportUploadPartReceipt>> PutPartAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid uploadId, int index,
        long? contentLength, Stream source, Action<long> limitBody, CancellationToken token)
    {
        var read = await store.ReadUploadAsync(libraryId, caller, uploadId, token).ConfigureAwait(false);
        if (read is not HostOutcome<ImportUpload>.Success { Value: var upload })
            return read.PropagateFailure<ImportUploadPartReceipt>();
        if (index < 0 || index >= upload.Files.Length || contentLength != upload.Files[index].Size)
            return new HostOutcome<ImportUploadPartReceipt>.Rejected("invalid-import-part");
        limitBody(upload.Files[index].Size);
        return await staging.CopyAsync(upload, index, source, token).ConfigureAwait(false);
    }

    /// <summary>Returns a stable job receipt for a verified upload, including after response loss.</summary>
    public async Task<HostOutcome<StartImportReceipt>> StartAsync(Guid libraryId, AuthenticatedHostInstance caller,
        StartImportRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<StartImportReceipt>.Forbidden();
        if (request.OperationId == Guid.Empty || request.UploadId == Guid.Empty)
            return new HostOutcome<StartImportReceipt>.Rejected("invalid-import-start");
        var prior = await store.ReadStartReceiptAsync(libraryId, caller, request, token).ConfigureAwait(false);
        if (prior is not HostOutcome<StartImportReceipt?>.Success { Value: var receipt })
            return prior.PropagateFailure<StartImportReceipt>();
        if (receipt is not null)
            return new HostOutcome<StartImportReceipt>.Success(receipt);
        var read = await store.ReadUploadAsync(libraryId, caller, request.UploadId, token).ConfigureAwait(false);
        if (read is not HostOutcome<ImportUpload>.Success { Value: var upload })
            return read.PropagateFailure<StartImportReceipt>();
        if (upload.JobId is null)
        {
            var verified = await staging.VerifyAsync(upload, token).ConfigureAwait(false);
            if (verified is not HostOutcome<bool>.Success { Value: true })
                return verified is HostOutcome<bool>.Success ? new HostOutcome<StartImportReceipt>.Rejected("import-upload-incomplete") :
                    verified.PropagateFailure<StartImportReceipt>();
        }
        return await store.AcceptImportAsync(upload, caller, request, token).ConfigureAwait(false);
    }

    /// <summary>Reads persisted job state through the registered owner boundary.</summary>
    public Task<HostOutcome<ImportJobSnapshot>> ReadJobAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid jobId, CancellationToken token) =>
        store.ReadImportJobAsync(libraryId, caller, jobId, token);

    /// <summary>Requeues the same failed job at the expected revision and keeps its original execution identity.</summary>
    /// <remarks>2026-11-24: Preserve native uncertainty when retrying FR-BEETS-001#ac-recover.</remarks>
    public Task<HostOutcome<RetryImportReceipt>> RetryAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        RetryImportRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<RetryImportReceipt>>(new HostOutcome<RetryImportReceipt>.Forbidden());
        if (jobId == Guid.Empty || request.OperationId == Guid.Empty || request.ExpectedRevision <= 0)
            return Task.FromResult<HostOutcome<RetryImportReceipt>>(new HostOutcome<RetryImportReceipt>.Rejected("invalid-import-retry"));
        return jobs.RetryImportAsync(libraryId, jobId, caller, request, token);
    }

    /// <summary>Validates a private import manifest entry for FR-BEETS-001#ac-import and FR-LIBRARY-002#ac-batch-commit.</summary>
    internal static bool ValidManifestFile(ImportUploadFile? file) => file is { Name.Length: > 0 and <= 255, Size: > 0, Sha256.Length: 64 } &&
        !string.IsNullOrWhiteSpace(file.Name) && file.Name is not ("." or "..") && !file.Name.Any(c => char.IsControl(c) || c is '/' or '\\') &&
        file.Sha256.All(Uri.IsHexDigit);
}
