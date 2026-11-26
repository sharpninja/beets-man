using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Acquires a local package selection and validates a private immutable archive copy.</summary>
/// <remarks>2026-11-25: Bind ZIP admission and exact package identity for FR-PLUGINS-001#ac-manage and TR-PLUGINS-ARCH-001.</remarks>
public sealed partial class LocalPluginPackages : IPluginPackageIntake
{
    private const int MaximumArchiveBytes = 64 * 1024 * 1024;
    private const long MaximumExpandedBytes = 512 * 1024 * 1024;
    private const int MaximumManifestBytes = 32 * 1024;
    private const int MaximumEntries = 1024;
    private const string ManifestName = "plugin.json";
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12,
        Converters =
        {
            new JsonStringEnumConverter<PluginContributionKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<PluginPanelFieldKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };
    private readonly string packageDirectory;
    private readonly ConcurrentDictionary<Guid, Selection> selections = new();
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Scopes private archives to the directory of an absolute desktop settings path.</summary>
    public LocalPluginPackages(string settingsPath)
    {
        if (!Path.IsPathFullyQualified(settingsPath) || Path.GetDirectoryName(settingsPath) is not { } parent)
            throw new ArgumentException("The desktop settings path must be absolute.", nameof(settingsPath));
        packageDirectory = Path.Combine(parent, "plugins", "archives");
    }

    /// <summary>Captures the absolute file selected by a local package picker without reading or executing it.</summary>
    public Guid SelectPackage(string archivePath)
    {
        if (!Path.IsPathFullyQualified(archivePath))
            throw new ArgumentException("The selected package path must be absolute.", nameof(archivePath));
        var token = Guid.NewGuid();
        selections[token] = new(Path.GetFullPath(archivePath));
        return token;
    }

    /// <inheritdoc />
    public async Task<Outcome<PluginPackage>> StageAsync(Guid selectionToken, CancellationToken token)
    {
        if (!selections.TryGetValue(selectionToken, out var selection))
            return Invalid("plugin-package-selection-not-found");
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (selection.Package is { } package)
                    return new Outcome<PluginPackage>.Success(package);
                var result = await StageFileAsync(selection.Source, token).ConfigureAwait(false);
                if (result is Outcome<PluginPackage>.Success accepted)
                    selection.Package = accepted.Value;
                return result;
            }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<PluginPackage>.Cancelled(); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        { return Invalid("invalid-plugin-package"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new Outcome<PluginPackage>.DependencyFailure("plugin-package-unavailable", true); }
    }

    private async Task<Outcome<PluginPackage>> StageFileAsync(string sourcePath, CancellationToken token)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        if (source.Length is 0 or > MaximumArchiveBytes)
            return Invalid("invalid-plugin-package-size");
        Directory.CreateDirectory(packageDirectory);
        var temp = Path.Combine(packageDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            PluginPackage package;
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var copy = new FileStream(temp, options))
            {
                await CopyBoundedAsync(source, copy, token).ConfigureAwait(false);
                copy.Flush(flushToDisk: true);
                copy.Position = 0;
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(copy, token).ConfigureAwait(false));
                copy.Position = 0;
                using var archive = new ZipArchive(copy, ZipArchiveMode.Read, leaveOpen: true);
                var inspected = await InspectAsync(archive, digest, token).ConfigureAwait(false);
                if (inspected is not Outcome<PluginPackage>.Success valid)
                    return inspected;
                package = valid.Value;
            }

            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(packageDirectory, $"{package.ArchiveSha256}.zip");
            if (File.Exists(destination))
            {
                await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
                if (existing.Length != new FileInfo(temp).Length ||
                    Convert.ToHexString(await SHA256.HashDataAsync(existing, token).ConfigureAwait(false)) != package.ArchiveSha256)
                    return new Outcome<PluginPackage>.DependencyFailure("plugin-archive-digest-mismatch", false);
            }
            else
            {
                File.Move(temp, destination);
            }
            return new Outcome<PluginPackage>.Success(package);
        }
        finally { File.Delete(temp); }
    }

    private static async Task CopyBoundedAsync(FileStream source, FileStream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                if (destination.Position > MaximumArchiveBytes - count)
                    throw new InvalidDataException();
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<Outcome<PluginPackage>> InspectAsync(ZipArchive archive, string digest, CancellationToken token)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries)
            return Invalid("invalid-plugin-package-size");
        var paths = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName;
            var directory = name.EndsWith('/');
            var path = directory ? name[..^1] : name;
            var unixType = entry.ExternalAttributes >> 16 & 0xf000;
            if (!ValidPath(path) || unixType != 0 && unixType != (directory ? 0x4000 : 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                entry.Length < 0 || directory && entry.Length != 0 || !paths.TryAdd(path, entry))
                return Invalid("invalid-plugin-package-path");
            if (entry.Length > MaximumExpandedBytes - expanded)
                return Invalid("invalid-plugin-package-size");
            expanded += entry.Length;
        }
        foreach (var path in paths.Keys)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                var parent = path[..slash];
                if (paths.TryGetValue(parent, out var entry) && entry.FullName != parent + "/")
                    return Invalid("invalid-plugin-package-path");
                slash = path.IndexOf('/', slash + 1);
            }
        }
        if (!paths.TryGetValue(ManifestName, out var manifest) || manifest.FullName != ManifestName ||
            manifest.Length is 0 or > MaximumManifestBytes)
            return Invalid("invalid-plugin-manifest");
        await using var json = manifest.Open();
        var bytes = new byte[MaximumManifestBytes + 1];
        var length = await json.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
        if (length != manifest.Length)
            return Invalid("invalid-plugin-manifest");
        var definition = JsonSerializer.Deserialize<PackageManifest>(bytes.AsSpan(0, length), ManifestOptions);
        if (definition is not { PluginId: { } id, Version: { } version, EntryPoint: { } entryPoint, Contributions: { Length: <= 32 } declared } ||
            !PluginRegistryHandlers.IsIdentifier(id) || !PluginRegistryHandlers.IsVersion(version) ||
            definition.MinimumProtocol < 1 || definition.MaximumProtocol < definition.MinimumProtocol ||
            entryPoint == ManifestName || !paths.TryGetValue(entryPoint, out var executable) || executable.FullName != entryPoint ||
            executable.Length == 0 || declared.Any(item => item is null || !PluginRegistryHandlers.IsIdentifier(item.ContributionId) ||
                !Enum.IsDefined(item.Kind) || item.HostAction is not null &&
                (item.Kind != PluginContributionKind.Action || item.HostAction != PluginHostActions.PausePlayback) ||
                !PluginPanelRules.IsValid(item, declared)) ||
            declared.Select(item => item.ContributionId).Distinct(StringComparer.Ordinal).Count() != declared.Length)
            return Invalid("invalid-plugin-manifest");
        return new Outcome<PluginPackage>.Success(new(id, version, definition.MinimumProtocol, definition.MaximumProtocol, entryPoint, [.. declared], digest));
    }

    private static bool ValidPath(string path) => path is { Length: > 0 and <= 512 } && path.IsNormalized(NormalizationForm.FormC) &&
        path.Split('/').All(part => part is { Length: > 0 and <= 120 } && part[^1] is not ('.' or ' ') &&
            !part.Any(character => char.IsControl(character) || character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') &&
            !ReservedFileName(part.Split('.', 2)[0]));

    private static bool ReservedFileName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" ||
            upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) &&
            upper[3] is >= '1' and <= '9';
    }

    private static Outcome<PluginPackage> Invalid(string code) => new Outcome<PluginPackage>.Invalid([new(code)]);

    private sealed class Selection(string source)
    {
        internal string Source { get; } = source;
        internal PluginPackage? Package { get; set; }
    }

    private sealed record PackageManifest(string? PluginId, string? Version, int MinimumProtocol, int MaximumProtocol, string? EntryPoint,
        PluginContribution[]? Contributions);
}
