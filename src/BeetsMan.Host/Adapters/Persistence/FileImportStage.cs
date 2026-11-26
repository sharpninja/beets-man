using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Identifies the verified album directory and its private paths in upload order.</summary>
/// <remarks>2026-11-24: Correlate native tag comparisons to the staged copy ordinal for FR-BEETS-001#ac-import.</remarks>
internal sealed record VerifiedImportAlbum(string Directory, ImmutableArray<string> Paths);

/// <summary>Stores verified copies outside the library and share folders.</summary>
/// <remarks>2026-11-24: Use generated private paths and atomic copy promotion for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Keep approved batch files separate from direct guided imports for FR-LIBRARY-002#ac-batch-commit.</remarks>
internal sealed class FileImportStage(string stageRoot, Guid? batchOperationId = null) : IImportFileStage
{
    /// <summary>Returns the staged album directory only after rechecking the immutable copy manifest.</summary>
    /// <remarks>2026-11-24: Admit verified local inputs to beets for FR-BEETS-001#ac-import.</remarks>
    internal async Task<HostOutcome<VerifiedImportAlbum>> OpenVerifiedAlbumAsync(ImportUpload upload, CancellationToken token)
    {
        var verified = await VerifyAsync(upload, token).ConfigureAwait(false);
        if (verified is not HostOutcome<bool>.Success { Value: true })
            return verified is HostOutcome<bool>.Success ? new HostOutcome<VerifiedImportAlbum>.Rejected("import-staged-copy-unavailable") :
                verified.PropagateFailure<VerifiedImportAlbum>();
        try
        {
            var directory = UploadDirectory(upload, create: false);
            var names = upload.Files.Select((part, index) => PartName(index, part.Name)).ToImmutableArray();
            var expected = names.ToHashSet(StringComparer.Ordinal);
            if (Directory.EnumerateFileSystemEntries(directory).Any(path => !expected.Contains(Path.GetFileName(path))))
                return new HostOutcome<VerifiedImportAlbum>.Rejected("import-staged-album-has-extra-files");
            return new HostOutcome<VerifiedImportAlbum>.Success(new(directory, [.. names.Select(name => Path.Combine(directory, name))]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new HostOutcome<VerifiedImportAlbum>.Unavailable("import-staging-unavailable"); }
    }
    /// <inheritdoc />
    public async Task<HostOutcome<ImportUploadPartReceipt>> CopyAsync(ImportUpload upload, int index, Stream source, CancellationToken token)
    {
        var expected = upload.Files[index];
        string? attempt = null;
        var ownsAttempt = false;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var directory = UploadDirectory(upload, create: true);
            var destination = Path.Combine(directory, PartName(index, expected.Name));
            attempt = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(attempt, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                ownsAttempt = true;
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                {
                    if (read > expected.Size - received)
                        return CopyMismatch();
                    digest.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    received += read;
                }
                if (received != expected.Size || Convert.ToHexString(digest.GetHashAndReset()) != expected.Sha256)
                    return CopyMismatch();
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(attempt, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                if (!await VerifyFileAsync(destination, expected, token).ConfigureAwait(false))
                    return new HostOutcome<ImportUploadPartReceipt>.Unavailable("import-staged-copy-unavailable");
            }
            return new HostOutcome<ImportUploadPartReceipt>.Success(new(upload.UploadId, index));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<ImportUploadPartReceipt>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new HostOutcome<ImportUploadPartReceipt>.Unavailable("import-staging-unavailable"); }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (ownsAttempt && attempt is not null)
            {
                try { File.Delete(attempt); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <inheritdoc />
    public async Task<HostOutcome<bool>> VerifyAsync(ImportUpload upload, CancellationToken token)
    {
        try
        {
            var directory = UploadDirectory(upload, create: false);
            for (var index = 0; index < upload.Files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var expected = upload.Files[index];
                if (!await VerifyFileAsync(Path.Combine(directory, PartName(index, expected.Name)), expected, token).ConfigureAwait(false))
                    return new HostOutcome<bool>.Success(false);
            }
            return new HostOutcome<bool>.Success(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<bool>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new HostOutcome<bool>.Unavailable("import-staging-unavailable"); }
    }

    private static HostOutcome<ImportUploadPartReceipt> CopyMismatch() => new HostOutcome<ImportUploadPartReceipt>.Rejected("import-copy-mismatch");

    private string UploadDirectory(ImportUpload upload, bool create)
    {
        var path = stageRoot;
        var library = upload.LibraryId.ToString("N");
        var owner = upload.OwnerId.ToString("N");
        var part = upload.UploadId.ToString("N");
        var segments = batchOperationId is { } batch
            ? new[] { "approved-batch-imports", library, owner, batch.ToString("N"), part }
            : new[] { "imports", library, owner, part };
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            var directory = new DirectoryInfo(path);
            if (directory.LinkTarget is not null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An import staging directory is redirected.");
            if (create && !directory.Exists)
            {
                if (OperatingSystem.IsWindows())
                    directory.Create();
                else
                    Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        return path;
    }

    private static string PartName(int index, string sourceName)
    {
        var extension = Path.GetExtension(sourceName);
        return $"{index:D8}" + (extension is { Length: > 1 and <= 12 } && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension.ToLowerInvariant() : ".media");
    }

    private static async Task<bool> VerifyFileAsync(string path, ImportUploadFile expected, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expected.Size || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) == expected.Sha256;
    }
}
