using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Retains immutable device export payloads in a host-private staging directory.</summary>
/// <remarks>2026-11-24: Stream reviewed media and preview descriptors outside the shared destination for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed class FileStagedPlaylistContent : IDeviceExportPayloadStore
{
    private readonly string directory;

    /// <summary>Creates a content store rooted in an absolute host-local staging path.</summary>
    /// <param name="directory">The host-owned staging directory outside all active shares.</param>
    public FileStagedPlaylistContent(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The private staging path must be absolute.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(ImmutableArray<byte> content, CancellationToken token)
    {
        using var stream = new MemoryStream(ImmutableCollectionsMarshal.AsArray(content) ?? [], writable: false);
        return await StoreAsync(stream, token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> StoreAsync(Stream content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        var pending = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
        try
        {
            string identity;
            await using (pending.ConfigureAwait(false))
            {
                await content.CopyToAsync(pending, token).ConfigureAwait(false);
                pending.Flush(flushToDisk: true);
                pending.Position = 0;
                identity = Convert.ToHexString(await SHA256.HashDataAsync(pending, token).ConfigureAwait(false));
            }
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, identity);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                await VerifyAsync(destination, identity, token).ConfigureAwait(false);
            }
            return identity;
        }
        finally { File.Delete(temporary); }
    }

    /// <inheritdoc />
    public ValueTask<Stream?> OpenReadAsync(OwnedDeviceExportFile file, CancellationToken token) => OpenDigestAsync(file.ContentIdentity, token);

    /// <inheritdoc />
    public ValueTask<Stream?> OpenDigestAsync(string digest, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            return ValueTask.FromResult<Stream?>(null);
        var path = Path.Combine(directory, digest.ToUpperInvariant());
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return ValueTask.FromResult<Stream?>(null);
        return ValueTask.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Staged export content must be a regular file.");
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        if (Convert.ToHexString(hash) != expected)
            throw new IOException("The existing export payload does not match its content identity.");
    }
}
