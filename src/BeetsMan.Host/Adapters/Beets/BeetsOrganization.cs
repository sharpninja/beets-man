using System.Security.Cryptography;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Organizes a track by copying the approved bytes and repointing the same native record.</summary>
/// <remarks>2026-11-24: Preserve the originals and reconcile exact organization paths for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed partial class BeetsGuidedImport : IBeetsOrganization
{
    /// <inheritdoc />
    async Task<HostOutcome<OrganizationCopy>> IBeetsOrganization.InspectAsync(MaintenanceTrack track, MaintenancePathChange move, CancellationToken token)
    {
        try
        {
            var observed = await ReadCatalogPathAsync(track.BeetsTrackId, token).ConfigureAwait(false);
            if (observed is not HostOutcome<string?>.Success { Value: var path })
                return observed.PropagateFailure<OrganizationCopy>();
            var source = LibraryMediaPath(track.RelativePath);
            if (source is null || !SamePath(path, source) || !File.Exists(source) || move.TrackId != track.TrackId ||
                !SamePath(move.Source, track.RelativePath))
                return new HostOutcome<OrganizationCopy>.Rejected("organization-source-changed", Conflict: true);
            var destination = LibraryMediaPath(move.Destination);
            if (destination is null || File.Exists(destination) || SamePath(source, destination))
                return new HostOutcome<OrganizationCopy>.Rejected("organization-destination-occupied", Conflict: true);
            var stamp = await ReadMediaFingerprintAsync(source, token).ConfigureAwait(false);
            return stamp is not null
                ? new HostOutcome<OrganizationCopy>.Success(new(track, move.Destination, stamp.Size, stamp.Sha256))
                : new HostOutcome<OrganizationCopy>.Rejected("organization-source-changed", Conflict: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<OrganizationCopy>.Unavailable("organization-source-unavailable"); }
    }

    /// <inheritdoc />
    async Task<HostOutcome<OrganizationFileStamp>> IBeetsOrganization.ReadFileStampAsync(string relativePath, CancellationToken token)
    {
        try
        {
            var path = LibraryMediaPath(relativePath);
            if (path is not null && await ReadMediaFingerprintAsync(path, token).ConfigureAwait(false) is { } stamp)
                return new HostOutcome<OrganizationFileStamp>.Success(stamp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new HostOutcome<OrganizationFileStamp>.Unavailable("organization-file-unverified");
    }

    /// <inheritdoc />
    async Task<HostOutcome<NativeOrganizationState>> IBeetsOrganization.ObserveAsync(OrganizationCopy copy, CancellationToken token)
    {
        try { return await ObserveOrganizationAsync(copy, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<NativeOrganizationState>.Unavailable("organization-readback-unavailable"); }
    }

    /// <inheritdoc />
    async Task<HostOutcome<bool>> IBeetsOrganization.ApplyAsync(OrganizationCopy copy, CancellationToken token)
    {
        try
        {
            var state = await ObserveOrganizationAsync(copy, token).ConfigureAwait(false);
            if (state is not HostOutcome<NativeOrganizationState>.Success { Value: NativeOrganizationState.Source })
                return new HostOutcome<bool>.Unavailable("organization-source-changed");
            var source = LibraryMediaPath(copy.Track.RelativePath);
            var destination = LibraryMediaPath(copy.Destination);
            if (source is null || destination is null)
                return new HostOutcome<bool>.Unavailable("organization-path-unavailable");
            if (!File.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!SamePath(LibraryMediaPath(copy.Destination), destination))
                    return new HostOutcome<bool>.Unavailable("organization-path-unavailable");
                await using var original = OpenOrganizationSource(source);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await original.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (!await MatchesOrganizationFileAsync(source, copy, token).ConfigureAwait(false) ||
                !await MatchesOrganizationFileAsync(destination, copy, token).ConfigureAwait(false))
                return new HostOutcome<bool>.Unavailable("organization-copy-unverified");

            var literal = destination.Replace("$", "$$", StringComparison.Ordinal).Replace("%", "$%", StringComparison.Ordinal);
            // Both switches are explicit so the CLI updates only the scoped catalog field and never moves media or art.
            var result = await RunAsync(["modify", "-M", "-W", "-y", "--", TrackQuery(copy.Track.BeetsTrackId),
                ExactMaintenancePathQuery(copy.Track.RelativePath), $"path={literal}"], false, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.Started)
                return new HostOutcome<bool>.Unavailable("organization-native-write-unavailable");
            return await ObserveOrganizationAsync(copy, token).ConfigureAwait(false) is HostOutcome<NativeOrganizationState>.Success
                { Value: NativeOrganizationState.Destination } ? new HostOutcome<bool>.Success(true) :
                new HostOutcome<bool>.Unavailable("organization-reconciliation-incomplete");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new HostOutcome<bool>.Unavailable("organization-copy-unavailable"); }
    }

    /// <summary>Reconciles both file digests and the native path against the stored manifest.</summary>
    private async Task<HostOutcome<NativeOrganizationState>> ObserveOrganizationAsync(OrganizationCopy copy, CancellationToken token)
    {
        var source = LibraryMediaPath(copy.Track.RelativePath);
        var destination = LibraryMediaPath(copy.Destination);
        if (source is null || destination is null || SamePath(source, destination) || !await MatchesOrganizationFileAsync(source, copy, token).ConfigureAwait(false))
            return new HostOutcome<NativeOrganizationState>.Success(NativeOrganizationState.Diverged);
        var observed = await ReadCatalogPathAsync(copy.Track.BeetsTrackId, token).ConfigureAwait(false);
        if (observed is not HostOutcome<string?>.Success { Value: var actual })
            return observed.PropagateFailure<NativeOrganizationState>();
        var exists = File.Exists(destination);
        if (exists && !await MatchesOrganizationFileAsync(destination, copy, token).ConfigureAwait(false))
            return new HostOutcome<NativeOrganizationState>.Success(NativeOrganizationState.Diverged);
        if (SamePath(actual, source))
            return new HostOutcome<NativeOrganizationState>.Success(NativeOrganizationState.Source);
        return new HostOutcome<NativeOrganizationState>.Success(exists && SamePath(actual, destination)
            ? NativeOrganizationState.Destination : NativeOrganizationState.Diverged);
    }

    /// <summary>Resolves only ordinary files and directories below the configured music root.</summary>
    private string? LibraryMediaPath(string relative)
    {
        if (!HostLibraryMaintenance.IsLibraryRelativePath(relative))
            return null;
        var path = musicRoot;
        var parts = relative.Split('/');
        foreach (var part in parts[..^1])
        {
            path = Path.Combine(path, part);
            var directory = new DirectoryInfo(path);
            if (directory.LinkTarget is not null || File.Exists(path) || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        }
        var target = new FileInfo(Path.Combine(path, parts[^1]));
        return target.LinkTarget is not null || Directory.Exists(target.FullName) || target.Exists && (target.Attributes & FileAttributes.ReparsePoint) != 0
            ? null : target.FullName;
    }

    /// <summary>Checks the exact length and SHA-256 for the stored approval checkpoint.</summary>
    private static async Task<bool> MatchesOrganizationFileAsync(string path, OrganizationCopy copy, CancellationToken token)
    {
        var expected = new OrganizationFileStamp(copy.Size, copy.Sha256);
        return await ReadMediaFingerprintAsync(path, token, expected.Size).ConfigureAwait(false) == expected;
    }

    /// <summary>Reads stable, nonempty file bytes without writing media.</summary>
    private static async Task<OrganizationFileStamp?> ReadMediaFingerprintAsync(string path, CancellationToken token, long? expectedLength = null)
    {
        if (!File.Exists(path))
            return null;
        await using var file = OpenOrganizationSource(path);
        var length = file.Length;
        if (length <= 0 || expectedLength.HasValue && expectedLength.Value != length)
            return null;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        return file.Position == length && file.Length == length ? new(length, digest) : null;
    }

    /// <summary>Opens media read-only with sequential access for copy verification.</summary>
    private static FileStream OpenOrganizationSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Checks normalized paths with the configured catalog platform's comparison.</summary>
    private static bool SamePath(string? first, string second) => ImportCatalogPublication.PathComparer.Equals(first, second);
}
