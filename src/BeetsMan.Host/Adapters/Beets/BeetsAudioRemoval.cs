using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Deletes only the reviewed audio and refuses paths still claimed by native catalog entries.</summary>
/// <remarks>2026-11-24: Pin and verify explicit media deletion for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed partial class BeetsGuidedImport : IAudioRemovalFiles
{
    /// <inheritdoc />
    async Task<HostOutcome<NativeAudioFileState>> IAudioRemovalFiles.ObserveAudioFileAsync(AudioRemovalFile file, CancellationToken token)
    {
        try { return new HostOutcome<NativeAudioFileState>.Success(await ReadAudioStateAsync(file, token).ConfigureAwait(false)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<NativeAudioFileState>.Unavailable("audio-removal-readback-unavailable"); }
    }

    /// <inheritdoc />
    async Task<HostOutcome<bool>> IAudioRemovalFiles.DeleteAudioFileAsync(AudioRemovalFile file, CancellationToken token)
    {
        try
        {
            var path = LibraryMediaPath(file.RelativePath);
            if (path is null)
                return new HostOutcome<bool>.Rejected("audio-removal-baseline-changed", Conflict: true);
            var references = await RunAsync(["list", "-f", MaintenanceIdentityFormat, "--", ExactMaintenancePathQuery(file.RelativePath)], true, token)
                .ConfigureAwait(false);
            if (!references.Succeeded || references.Records.Length != 0)
                return new HostOutcome<bool>.Unavailable("audio-removal-path-still-referenced");
            var state = await ReadAudioStateAsync(file, token).ConfigureAwait(false);
            if (state == NativeAudioFileState.Diverged || !SamePath(LibraryMediaPath(file.RelativePath), path))
                return new HostOutcome<bool>.Rejected("audio-removal-baseline-changed", Conflict: true);
            if (state == NativeAudioFileState.Present)
            {
                token.ThrowIfCancellationRequested();
                File.Delete(path);
            }
            return new HostOutcome<bool>.Success(await ReadAudioStateAsync(file, token).ConfigureAwait(false) == NativeAudioFileState.Missing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<bool>.Unavailable("audio-removal-delete-unavailable"); }
    }

    /// <summary>Distinguishes verified absence from an inaccessible, replaced or redirected file.</summary>
    private async Task<NativeAudioFileState> ReadAudioStateAsync(AudioRemovalFile file, CancellationToken token)
    {
        var path = LibraryMediaPath(file.RelativePath);
        if (path is null)
            return NativeAudioFileState.Diverged;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return NativeAudioFileState.Diverged;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return NativeAudioFileState.Missing; }
        var read = await ReadMediaFingerprintAsync(path, token, file.Size).ConfigureAwait(false);
        return read is not null && read.Sha256 == file.Sha256 ? NativeAudioFileState.Present : NativeAudioFileState.Diverged;
    }
}
