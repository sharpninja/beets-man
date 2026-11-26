using System.IO.Compression;
using System.Security.Cryptography;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;

namespace BeetsMan.Desktop.Adapters.Configuration;

public sealed partial class LocalPluginPackages
{
    /// <summary>Rechecks the pinned bytes and unpacks only validated relative paths into a private worker directory.</summary>
    /// <remarks>2026-11-25: Preserve the installed identity from trust to worker launch for FR-PLUGINS-001#ac-manage and ac-recovery.</remarks>
    internal async Task<Outcome<PluginWorkerFiles>> PrepareWorkerAsync(PluginPackage package, CancellationToken token)
    {
        if (package.ArchiveSha256.Length != 64 || !package.ArchiveSha256.All(char.IsAsciiHexDigitUpper) ||
            !package.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new Outcome<PluginWorkerFiles>.Invalid([new("invalid-plugin-worker-entry-point")]);

        PluginWorkerFiles? staged = null;
        try
        {
            var path = Path.Combine(packageDirectory, $"{package.ArchiveSha256}.zip");
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            if (source.Length is 0 or > MaximumArchiveBytes || Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)) != package.ArchiveSha256)
                return new Outcome<PluginWorkerFiles>.DependencyFailure("plugin-archive-digest-mismatch", false);
            source.Position = 0;
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var inspected = await InspectAsync(archive, package.ArchiveSha256, token).ConfigureAwait(false);
            if (inspected is not Outcome<PluginPackage>.Success pin ||
                pin.Value with { Contributions = package.Contributions } != package ||
                !PluginPanelRules.SameDeclarations(pin.Value.Contributions, package.Contributions))
                return new Outcome<PluginWorkerFiles>.DependencyFailure("plugin-archive-manifest-mismatch", false);

            var workerDirectory = Path.Combine(Path.GetDirectoryName(packageDirectory)!, "workers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workerDirectory);
            staged = new(workerDirectory, Path.Combine(workerDirectory, package.EntryPoint));
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(workerDirectory, entry.FullName);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                await CopyExactAsync(input, output, entry.Length, token).ConfigureAwait(false);
            }
            var result = new Outcome<PluginWorkerFiles>.Success(staged);
            staged = null;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PluginWorkerFiles>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { return new Outcome<PluginWorkerFiles>.DependencyFailure("plugin-worker-files-unavailable", true); }
        finally { staged?.Dispose(); }
    }

    private static async Task CopyExactAsync(Stream input, Stream target, long expected, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (expected > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected)), token).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException();
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            expected -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException();
    }
}

/// <summary>Owns only the private unpacked copy for one worker activation.</summary>
internal sealed record PluginWorkerFiles(string DirectoryPath, string EntryPoint) : IDisposable
{
    public void Dispose()
    {
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
