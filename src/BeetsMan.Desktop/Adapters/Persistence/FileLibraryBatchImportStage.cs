using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Copies picked media into a desktop-owned directory outside the synchronized music tree.</summary>
/// <remarks>2026-11-24: Verify and retain the exact pending import bytes for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
public sealed class FileLibraryBatchImportStage : ILibraryBatchImportFiles
{
    private const int BufferSize = 64 * 1024;
    private readonly string root;

    /// <summary>Binds a private absolute directory disjoint from the active music root.</summary>
    public FileLibraryBatchImportStage(string stageRoot, string musicRoot)
    {
        if (!Path.IsPathFullyQualified(stageRoot) || !Path.IsPathFullyQualified(musicRoot))
            throw new ArgumentException("Batch staging and music require absolute paths.");
        root = Path.GetFullPath(stageRoot);
        var music = Path.GetFullPath(musicRoot);
        if (IsWithin(root, music) || IsWithin(music, root))
            throw new ArgumentException("Batch staging and music must use separate directory trees.");
    }

    /// <inheritdoc />
    public async Task<Outcome<ImmutableArray<LibraryBatchImportFile>>> CopyAsync(LibraryBatchScope scope, Guid operationId,
        ImmutableArray<string> sources, CancellationToken token)
    {
        if (!Valid(scope, operationId) || sources.IsDefaultOrEmpty || sources.Any(path => string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) || !ValidName(Path.GetFileName(path))))
            return Invalid<ImmutableArray<LibraryBatchImportFile>>("invalid-import-source-selection");
        var cleared = await DiscardAsync(scope, operationId, token).ConfigureAwait(false);
        if (cleared is Outcome<bool>.Cancelled)
            return new Outcome<ImmutableArray<LibraryBatchImportFile>>.Cancelled();
        if (cleared is not Outcome<bool>.Success { Value: true })
            return Unavailable<ImmutableArray<LibraryBatchImportFile>>();

        try
        {
            var folder = OperationDirectory(scope, operationId, create: true);
            var files = ImmutableArray.CreateBuilder<LibraryBatchImportFile>(sources.Length);
            var buffer = new byte[BufferSize];
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                var draft = await CopyFileAsync(folder, source, buffer, token).ConfigureAwait(false);
                if (draft is null)
                    return Invalid<ImmutableArray<LibraryBatchImportFile>>("import-source-changed");
                var readback = await OpenVerifiedAsync(scope, operationId, draft, token).ConfigureAwait(false);
                if (readback is Outcome<Stream>.Cancelled)
                    return new Outcome<ImmutableArray<LibraryBatchImportFile>>.Cancelled();
                if (readback is not Outcome<Stream>.Success { Value: var verified })
                    return Unavailable<ImmutableArray<LibraryBatchImportFile>>();
                await verified.DisposeAsync().ConfigureAwait(false);
                files.Add(draft);
            }
            return new Outcome<ImmutableArray<LibraryBatchImportFile>>.Success(files.MoveToImmutable());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<ImmutableArray<LibraryBatchImportFile>>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return Unavailable<ImmutableArray<LibraryBatchImportFile>>(); }
    }

    /// <inheritdoc />
    public async Task<Outcome<Stream>> OpenVerifiedAsync(LibraryBatchScope scope, Guid operationId, LibraryBatchImportFile file, CancellationToken token)
    {
        if (!Valid(scope, operationId) || file.FileId == Guid.Empty || file.Size <= 0 || file.Sha256 is not { Length: 64 })
            return Invalid<Stream>("invalid-private-import-copy");
        FileStream? stream = null;
        try
        {
            var path = Path.Combine(OperationDirectory(scope, operationId, create: false), CopyName(file.FileId));
            CheckFile(path);
            stream = OpenSource(path);
            if (stream.Length != file.Size || Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != file.Sha256 ||
                stream.Position != file.Size || stream.Length != file.Size)
                return Invalid<Stream>("private-import-copy-changed");
            stream.Position = 0;
            var result = new Outcome<Stream>.Success(stream);
            stream = null;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<Stream>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return Unavailable<Stream>(); }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<Outcome<bool>> DiscardAsync(LibraryBatchScope scope, Guid operationId, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Cancelled());
        if (!Valid(scope, operationId))
            return Task.FromResult(Invalid<bool>("invalid-private-import-copy"));
        try
        {
            var folder = OperationDirectory(scope, operationId, create: false);
            if (Directory.Exists(folder))
            {
                var files = Directory.GetFileSystemEntries(folder);
                foreach (var path in files)
                {
                    if (Path.GetExtension(path) != ".copy" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                        return Task.FromResult(Unavailable<bool>());
                    CheckFile(path);
                }
                foreach (var path in files)
                    File.Delete(path);
                Directory.Delete(folder, recursive: false);
            }
            return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return Task.FromResult(Unavailable<bool>()); }
    }

    private static async Task<LibraryBatchImportFile?> CopyFileAsync(string folder, string source, byte[] buffer, CancellationToken token)
    {
        var fileId = Guid.NewGuid();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = OpenSource(source);
        var size = input.Length;
        if (size <= 0)
            return null;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = BufferSize };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var output = new FileStream(Path.Combine(folder, CopyName(fileId)), options))
        {
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                if (read > size - copied)
                    return null;
                copied += read;
                digest.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (copied != size || input.Length != size)
                return null;
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        var hash = Convert.ToHexString(digest.GetHashAndReset());
        input.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == hash && input.Position == size && input.Length == size
            ? new(fileId, Path.GetFileName(source), size, hash) : null;
    }

    private string OperationDirectory(LibraryBatchScope scope, Guid operationId, bool create)
    {
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            CheckDirectory(ancestor);
        EnsureDirectory(root, create);
        var library = Path.Combine(root, scope.LibraryId.ToString("N"));
        EnsureDirectory(library, create);
        var origin = Path.Combine(library, scope.OriginInstanceId.ToString("N"));
        EnsureDirectory(origin, create);
        var operation = Path.Combine(origin, operationId.ToString("N"));
        EnsureDirectory(operation, create);
        return operation;
    }

    private static void EnsureDirectory(string path, bool create)
    {
        var folder = new DirectoryInfo(path);
        CheckDirectory(folder);
        if (!create || folder.Exists)
            return;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(path);
    }

    private static void CheckDirectory(DirectoryInfo folder)
    {
        if (folder.LinkTarget is not null || folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The private import directory is redirected.");
    }

    private static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The private import file is redirected or is a directory.");
    }

    private static bool IsWithin(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool Valid(LibraryBatchScope scope, Guid operationId) =>
        scope.LibraryId != Guid.Empty && scope.OriginInstanceId != Guid.Empty && operationId != Guid.Empty;

    private static bool ValidName(string name) => name.Length is > 0 and <= 255 && !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") && !name.Any(character => char.IsControl(character) || character is '/' or '\\');

    private static string CopyName(Guid fileId) => $"{fileId:N}.copy";

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);
    private static Outcome<T> Unavailable<T>() => new Outcome<T>.DependencyFailure("private-import-stage-unavailable", true);
}
