using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Applies one operation to its registered export root and verifies the resulting file state.</summary>
/// <remarks>2026-11-24: Retain verified originals and retry interrupted effects for FR-FILESYNC-001#ac-safety.</remarks>
public sealed class FileDeviceExportStepWriter : IDeviceExportStepWriter
{
    private readonly string shareRoot;
    private readonly string privateRoot;
    private readonly IDeviceExportContentReader content;
    private readonly FileDeviceExportSnapshotReader reader;
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Binds a dedicated export root and a separate existing private work directory to this registered receiver.</summary>
    public FileDeviceExportStepWriter(string shareRoot, string privateRoot, RegisteredAndroidExport owner, IDeviceExportContentReader content)
    {
        reader = new(shareRoot, owner);
        if (!Path.IsPathFullyQualified(privateRoot))
            throw new ArgumentException("Publication work must be stored under an absolute private path.", nameof(privateRoot));
        this.shareRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shareRoot));
        this.privateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(privateRoot));
        if (!IsOutside(this.privateRoot, this.shareRoot) || !IsOutside(this.shareRoot, this.privateRoot))
            throw new ArgumentException("The publication work directory and active share must be disjoint.", nameof(privateRoot));
        this.content = content;
    }

    /// <inheritdoc />
    public async Task<HostOutcome<bool>> ApplyAsync(PendingDeviceExportPublication work, int ordinal, CancellationToken token)
    {
        if (work.PublicationId == Guid.Empty || work.Operations.IsDefault || ordinal < 0 || ordinal >= work.Operations.Length)
            return new HostOutcome<bool>.Rejected("invalid-export-publication-step");
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await ApplyOnLaneAsync(work, ordinal, token).ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<bool>.Cancelled(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<bool>.Unavailable("export-file-write-unavailable");
        }
    }

    private async Task<HostOutcome<bool>> ApplyOnLaneAsync(PendingDeviceExportPublication work, int ordinal, CancellationToken token)
    {
        var operation = work.Operations[ordinal];
        var comparer = work.Destination.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        if (!ValidOperation(operation, comparer))
            return new HostOutcome<bool>.Rejected("invalid-export-publication-step");
        var observed = await InspectAsync(work.Destination, operation, comparer, token).ConfigureAwait(false);
        if (observed is not HostOutcome<StepState>.Success { Value: var state })
            return observed.PropagateFailure<bool>();

        if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(new(privateRoot)))
            return new HostOutcome<bool>.Unavailable("export-private-work-unavailable");
        var directory = Directory.CreateDirectory(Path.Combine(privateRoot, work.PublicationId.ToString("N")));
        if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(directory))
            return new HostOutcome<bool>.Unavailable("export-private-work-unavailable");
        var step = ordinal.ToString(CultureInfo.InvariantCulture);
        var marker = Path.Combine(directory.FullName, $"{step}.started");
        var retained = Path.Combine(directory.FullName, $"{step}.original");
        var markerIdentity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation)));
        var started = Path.Exists(marker);
        if (started && !await MatchesMarkerAsync(marker, markerIdentity, token).ConfigureAwait(false))
            return new HostOutcome<bool>.Unavailable("export-private-work-unavailable");
        if (started && operation.Before is { } expectedBefore && !await MatchesDigestAsync(retained, expectedBefore.ContentIdentity, token).ConfigureAwait(false))
            return new HostOutcome<bool>.Unavailable("export-private-work-unavailable");

        if (IsFinal(operation, state))
            return started ? new HostOutcome<bool>.Success(true) : Changed();
        if (!CanApply(operation, state, started))
            return Changed();

        if (!started && operation.Before is { } original && !await RetainAsync(state.BeforePath!, retained, original.ContentIdentity, token).ConfigureAwait(false))
            return Changed();
        if (operation.After is { } after && !state.AfterMatches)
        {
            await using var source = operation.Kind == DeviceExportOperationKind.Move
                ? File.OpenRead(retained)
                : await content.OpenReadAsync(after, token).ConfigureAwait(false);
            if (source is null)
                return new HostOutcome<bool>.Unavailable("export-staged-bytes-unavailable");
            var install = await InstallAsync(work.Destination, operation, comparer, state.AfterPath!, source,
                started ? null : marker, markerIdentity, token).ConfigureAwait(false);
            if (install is not HostOutcome<bool>.Success)
                return install;
        }

        if (operation.Kind is DeviceExportOperationKind.Remove or DeviceExportOperationKind.Move)
        {
            var latest = await InspectAsync(work.Destination, operation, comparer, token).ConfigureAwait(false);
            if (latest is not HostOutcome<StepState>.Success { Value: var checkedState })
                return latest.PropagateFailure<bool>();
            if (operation.Kind == DeviceExportOperationKind.Move && !checkedState.AfterMatches)
                return Changed();
            if (checkedState.BeforeExists)
            {
                if (!checkedState.BeforeMatches)
                    return Changed();
                if (operation.Kind == DeviceExportOperationKind.Remove && !started)
                    await WriteMarkerAsync(marker, markerIdentity, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Delete(checkedState.BeforePath!);
            }
            else if (!started && operation.Kind == DeviceExportOperationKind.Remove)
                return Changed();
        }

        var final = await InspectAsync(work.Destination, operation, comparer, token).ConfigureAwait(false);
        if (final is not HostOutcome<StepState>.Success { Value: var result })
            return final.PropagateFailure<bool>();
        return IsFinal(operation, result) ? new HostOutcome<bool>.Success(true) : Changed();
    }

    private async Task<HostOutcome<bool>> InstallAsync(RegisteredAndroidExport target, DeviceExportFileOperation operation, StringComparer comparer,
        string path, Stream bytes, string? marker, string markerIdentity, CancellationToken token)
    {
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(directory))
            return Changed();
        // Syncthing excludes its temporary-file namespace, so partial copies never become published entries.
        var temporary = Path.Combine(directory.FullName, $".syncthing.beets-{Guid.NewGuid():N}.tmp");
        var pending = OpenNewFile(temporary);
        try
        {
            await using (pending.ConfigureAwait(false))
            {
                if (!await CopyVerifiedAsync(bytes, pending, operation.After!.ContentIdentity, token).ConfigureAwait(false))
                    return new HostOutcome<bool>.Rejected("export-staged-bytes-changed", Conflict: true);
            }

            var observed = await InspectAsync(target, operation, comparer, token).ConfigureAwait(false);
            if (observed is not HostOutcome<StepState>.Success { Value: var current })
                return observed.PropagateFailure<bool>();
            if (current.AfterPath != path || !CanApply(operation, current, started: true) || current.AfterMatches)
                return Changed();
            if (marker is not null)
                await WriteMarkerAsync(marker, markerIdentity, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: operation.Kind == DeviceExportOperationKind.Replace);
            return new HostOutcome<bool>.Success(true);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<HostOutcome<StepState>> InspectAsync(RegisteredAndroidExport target, DeviceExportFileOperation operation,
        StringComparer comparer, CancellationToken token)
    {
        var paths = new[] { operation.Before?.Path, operation.After?.Path }.OfType<string>().ToArray();
        var read = await reader.ReadAsync(target, paths, token).ConfigureAwait(false);
        if (read is not HostOutcome<DeviceExportShareSnapshot>.Success { Value: var snapshot })
            return read.PropagateFailure<StepState>();

        var observed = snapshot.Paths.ToDictionary(item => item.Path, comparer);
        foreach (var path in paths)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (observed.TryGetValue(path[..slash], out var parent) && parent.Kind != DeviceExportPathKind.Directory)
                    return Changed().PropagateFailure<StepState>();
            }
        }
        var before = operation.Before is { } old ? observed.GetValueOrDefault(old.Path) : null;
        var after = operation.After is { } next ? observed.GetValueOrDefault(next.Path) : null;
        return new HostOutcome<StepState>.Success(new(FullPath(operation.Before?.Path, observed), FullPath(operation.After?.Path, observed),
            before is not null, after is not null, Matches(before, operation.Before), Matches(after, operation.After)));
    }

    private string? FullPath(string? requested, Dictionary<string, ObservedDeviceExportPath> observed)
    {
        if (requested is null)
            return null;
        var segments = requested.Split('/');
        var relative = segments[0];
        foreach (var segment in segments.Skip(1))
        {
            relative = observed.GetValueOrDefault(relative)?.Path ?? relative;
            relative += '/' + segment;
        }
        relative = observed.GetValueOrDefault(relative)?.Path ?? relative;
        return Path.Combine(shareRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool ValidOperation(DeviceExportFileOperation operation, StringComparer comparer) => operation.Kind switch
    {
        DeviceExportOperationKind.Add => operation.Before is null && operation.After is not null,
        DeviceExportOperationKind.Remove => operation.Before is not null && operation.After is null,
        DeviceExportOperationKind.Replace => operation.Before is { } before && operation.After is { } after && comparer.Equals(before.Path, after.Path),
        DeviceExportOperationKind.Move => operation.Before is { } original && operation.After is { } destination &&
            !comparer.Equals(original.Path, destination.Path) && original.Source == destination.Source &&
            SameDigest(original.ContentIdentity, destination.ContentIdentity),
        _ => false
    };

    private static bool CanApply(DeviceExportFileOperation operation, StepState state, bool started) => operation.Kind switch
    {
        DeviceExportOperationKind.Add => !state.AfterExists,
        DeviceExportOperationKind.Replace or DeviceExportOperationKind.Remove => state.BeforeMatches,
        DeviceExportOperationKind.Move => state.BeforeMatches && (!state.AfterExists || started && state.AfterMatches),
        _ => false
    };

    private static bool IsFinal(DeviceExportFileOperation operation, StepState state) => operation.Kind switch
    {
        DeviceExportOperationKind.Remove => !state.BeforeExists,
        DeviceExportOperationKind.Move => !state.BeforeExists && state.AfterMatches,
        _ => state.AfterMatches
    };

    private static bool Matches(ObservedDeviceExportPath? actual, OwnedDeviceExportFile? expected) =>
        actual is { Kind: DeviceExportPathKind.File, ContentIdentity: { } actualIdentity } && expected is not null &&
        SameDigest(actualIdentity, expected.ContentIdentity);

    private static bool SameDigest(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static HostOutcome<bool> Changed() => new HostOutcome<bool>.Rejected("export-file-changed", Conflict: true);

    private static bool IsOutside(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task<bool> RetainAsync(string source, string retained, string digest, CancellationToken token)
    {
        if (Path.Exists(retained))
            return await MatchesDigestAsync(retained, digest, token).ConfigureAwait(false);
        await using var original = File.OpenRead(source);
        var temporary = retained + $".{Guid.NewGuid():N}.tmp";
        var backup = OpenNewFile(temporary);
        try
        {
            await using (backup.ConfigureAwait(false))
            {
                if (!await CopyVerifiedAsync(original, backup, digest, token).ConfigureAwait(false))
                    return false;
            }
            File.Move(temporary, retained);
            return true;
        }
        finally { File.Delete(temporary); }
    }

    private static async Task WriteMarkerAsync(string path, string markerIdentity, CancellationToken token)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var stream = OpenNewFile(temporary);
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(markerIdentity), token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static FileStream OpenNewFile(string path) => new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);

    private static async Task<bool> CopyVerifiedAsync(Stream source, FileStream destination, string expected, CancellationToken token)
    {
        await source.CopyToAsync(destination, token).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        destination.Position = 0;
        return SameDigest(Convert.ToHexString(await SHA256.HashDataAsync(destination, token).ConfigureAwait(false)), expected);
    }

    private static async Task<bool> MatchesMarkerAsync(string path, string expected, CancellationToken token) =>
        IsPrivateFile(new FileInfo(path), out var length) && length == expected.Length &&
        await File.ReadAllTextAsync(path, token).ConfigureAwait(false) == expected;

    private static async Task<bool> MatchesDigestAsync(string path, string expected, CancellationToken token)
    {
        if (!IsPrivateFile(new FileInfo(path), out _))
            return false;
        await using var stream = File.OpenRead(path);
        return SameDigest(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), expected);
    }

    private static bool IsPrivateFile(FileInfo item, out long length)
    {
        length = item.Exists && (item.Attributes & FileAttributes.ReparsePoint) == 0 ? item.Length : 0;
        return length > 0;
    }

    private sealed record StepState(string? BeforePath, string? AfterPath, bool BeforeExists, bool AfterExists, bool BeforeMatches, bool AfterMatches);
}
