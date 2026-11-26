using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Prepares a real beets catalog in private storage and installs the pinned files without overwriting a destination.</summary>
/// <remarks>2026-11-25: Preserve existing native library bytes and reconcile exact setup artifacts for FR-BEETS-001#ac-open and ac-recover.</remarks>
internal sealed class BeetsLibrarySetupFiles(BeetsImportOptions options, string musicRoot, string workRoot, BeetsGuidedImport native)
    : IBeetsLibrarySetupFiles
{
    private const string StagedCatalogName = "library.db";
    private const string StagedConfigurationName = "config.yaml";

    /// <inheritdoc />
    public HostBeetsSettings Settings { get; } = new(options.CatalogPath, musicRoot, options.AlbumPathTemplate ?? "",
        ImmutableDictionary<string, string>.Empty.Add(BeetsConfigurationValues.Behavior,
            options.Matching is null ? BeetsConfigurationValues.KeepTags : BeetsConfigurationValues.ReviewSuggestedMatches),
        [], options.ConfigurationPath!);

    /// <inheritdoc />
    public ImmutableArray<string> AvailablePluginIds => options.AllowedPluginIds;

    /// <inheritdoc />
    public bool CanReviewSuggestedMatches => options.Matching is not null;

    /// <inheritdoc />
    public bool CanInitialize => options.CreateCatalogIfMissing;

    /// <inheritdoc />
    public async Task<HostOutcome<bool>> ProbePluginsAsync(HostBeetsSettings profile, CancellationToken token)
    {
        if (profile.EnabledPluginIds.IsEmpty)
            return new HostOutcome<bool>.Success(true);
        try
        {
            var folder = StagePath(Guid.NewGuid());
            CheckedFile(Path.Combine(folder, StagedConfigurationName), createParent: true);
            return new HostOutcome<bool>.Success(await native.ProbeLibraryPluginsAsync(folder, profile, token).ConfigureAwait(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<bool>.Unavailable("beets-library-plugin-probe-unavailable");
        }
    }

    /// <inheritdoc />
    public HostOutcome<BeetsLibraryPresence> Inspect()
    {
        try
        {
            var catalog = CheckedFile(Settings.CatalogPath, createParent: false);
            var config = CheckedFile(Settings.ConfigurationPath, createParent: false);
            return new HostOutcome<BeetsLibraryPresence>.Success(new(catalog.Exists, config.Exists, catalog.Exists && IsSqlite(catalog.FullName)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<BeetsLibraryPresence>.Unavailable("beets-library-inspection-unavailable");
        }
    }

    /// <inheritdoc />
    public async Task<HostOutcome<PreparedBeetsLibrary>> PrepareAsync(CancellationToken token)
    {
        try
        {
            var stageId = Guid.NewGuid();
            var stage = StagePath(stageId);
            var catalog = CheckedFile(Path.Combine(stage, StagedCatalogName), createParent: true);
            if (!await native.CreateEmptyLibraryAsync(stage, catalog.FullName, token).ConfigureAwait(false) || !IsSqlite(catalog.FullName))
                return new HostOutcome<PreparedBeetsLibrary>.Unavailable("beets-library-native-initialization-failed");
            var config = native.WriteLibraryConfiguration(stage);
            return new HostOutcome<PreparedBeetsLibrary>.Success(new(stageId, await HashAsync(catalog.FullName, token).ConfigureAwait(false),
                await HashAsync(config, token).ConfigureAwait(false)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<PreparedBeetsLibrary>.Unavailable("beets-library-preparation-unavailable");
        }
    }

    /// <inheritdoc />
    public async Task<HostOutcome<bool>> InstallAsync(PreparedBeetsLibrary prepared, CancellationToken token)
    {
        try
        {
            var directory = StagePath(prepared.StageId);
            var catalog = CheckedFile(Path.Combine(directory, StagedCatalogName), createParent: false);
            var config = CheckedFile(Path.Combine(directory, StagedConfigurationName), createParent: false);
            if (!await MatchesAsync(catalog, prepared.CatalogHash, token).ConfigureAwait(false) ||
                !await MatchesAsync(config, prepared.ConfigurationHash, token).ConfigureAwait(false))
                return new HostOutcome<bool>.Unavailable("beets-library-preparation-unavailable");

            var targetCatalog = CheckedFile(Settings.CatalogPath, createParent: true);
            var targetConfig = CheckedFile(Settings.ConfigurationPath, createParent: true);
            if (targetCatalog.Exists && !await MatchesAsync(targetCatalog, prepared.CatalogHash, token).ConfigureAwait(false) ||
                targetConfig.Exists && !await MatchesAsync(targetConfig, prepared.ConfigurationHash, token).ConfigureAwait(false))
                return new HostOutcome<bool>.Rejected("beets-library-destination-changed", Conflict: true);

            // Once an exclusive copy starts, finish that file so ordinary cancellation leaves a recoverable artifact.
            await InstallFileAsync(catalog, targetCatalog, token).ConfigureAwait(false);
            await InstallFileAsync(config, targetConfig, token).ConfigureAwait(false);
            return await MatchesAsync(targetCatalog, prepared.CatalogHash, token).ConfigureAwait(false) &&
                await MatchesAsync(targetConfig, prepared.ConfigurationHash, token).ConfigureAwait(false)
                ? new HostOutcome<bool>.Success(true) : new HostOutcome<bool>.Unavailable("beets-library-installation-unverified");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new HostOutcome<bool>.Unavailable("beets-library-installation-unavailable");
        }
    }

    private string StagePath(Guid stage) => Path.Combine(workRoot, "native-library-setup", stage.ToString("N"));

    private static FileInfo CheckedFile(string path, bool createParent)
    {
        var file = new FileInfo(path);
        var parent = file.Directory ?? throw new IOException("The library file needs an ordinary directory.");
        DirectoryInfo ancestor = parent;
        while (!ancestor.Exists)
        {
            if (ancestor.LinkTarget is not null || File.Exists(ancestor.FullName) || ancestor.Parent is not { } next)
                throw new IOException("The native library directory is obstructed.");
            ancestor = next;
        }
        if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(ancestor) || Directory.Exists(path) ||
            file.LinkTarget is not null || file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The native library path is redirected.");
        if (createParent)
        {
            Directory.CreateDirectory(parent.FullName);
            if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(new(parent.FullName)))
                throw new IOException("The native library directory is redirected.");
        }
        return file;
    }

    private static bool IsSqlite(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
            header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static async Task InstallFileAsync(FileInfo source, FileInfo target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target.Exists)
            return;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var input = source.OpenRead();
        await using var output = target.Open(options);
        await input.CopyToAsync(output, CancellationToken.None).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<bool> MatchesAsync(FileInfo file, string expected, CancellationToken token)
    {
        file.Refresh();
        return file.Exists && await HashAsync(file.FullName, token).ConfigureAwait(false) == expected;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }
}
