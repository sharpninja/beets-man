using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using YamlDotNet.Serialization;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Runs native beets imports and reads back the exact operation's verified file copies.</summary>
/// <remarks>2026-11-24: Implement native copying and recoverable catalog readback for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Record disc and year from the native import readback for FR-BEETS-001#ac-edit.</remarks>
/// <remarks>2026-11-25: Activate the saved native naming, import behavior and plugin profile for FR-BEETS-001#ac-open.</remarks>
internal sealed partial class BeetsGuidedImport(BeetsImportOptions options, string musicRoot, string workRoot, FileImportStage stage,
    MusicBrainzReleaseSearch? matching = null, Func<CancellationToken, Task<HostOutcome<BeetsNativeConfiguration>>>? readProfile = null) : IBeetsGuidedImport
{
    private const char ColumnSeparator = '\u001f';
    private const string RecordPrefix = "beetsman-import-v1";
    private const int MaxReadbackCharacters = 4 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);
    private static readonly string Format = string.Join(ColumnSeparator,
        RecordPrefix, "${id}", "${album_id}", "${path}", "${title}", "${artist}", "${album}", "${albumartist}", "${track}", "${genres}",
        "${mb_albumid}", "${mb_releasetrackid}", "${disc}", "${year}");
    private readonly string commandRoot = Path.Combine(workRoot, "beets-import", Guid.NewGuid().ToString("N"));
    private readonly object configurationGate = new();
    private (long Revision, string Path)? config;

    /// <summary>Runs beets against a private empty catalog before the setup transaction publishes its files.</summary>
    /// <remarks>2026-11-25: Create the real native SQLite catalog for FR-BEETS-001#ac-open.</remarks>
    internal async Task<bool> CreateEmptyLibraryAsync(string folder, string catalog, CancellationToken token)
    {
        var trial = Directory.CreateDirectory(Path.Combine(folder, "native")).FullName;
        var configuration = WriteBeetsConfig(trial, catalog, musicRoot, options.AlbumPathTemplate);
        var result = await RunAsync(["list"], captureReadback: false, token, configuration).ConfigureAwait(false);
        return result.Succeeded && File.Exists(catalog);
    }

    /// <summary>Writes the supported native settings with the production paths for the approved library.</summary>
    /// <remarks>2026-11-25: Generate the host configuration alongside its staged catalog for FR-BEETS-001#ac-open.</remarks>
    internal string WriteLibraryConfiguration(string folder) => WriteBeetsConfig(folder, options.CatalogPath, musicRoot, options.AlbumPathTemplate);

    /// <summary>Confirms that beets can load every approved native plugin name before saving an edit.</summary>
    /// <remarks>2026-11-25: Confirm loaded modules in the beets version output for FR-BEETS-001#ac-open.</remarks>
    internal async Task<bool> ProbeLibraryPluginsAsync(string folder, HostBeetsSettings profile, CancellationToken token)
    {
        var probe = WriteBeetsConfig(folder, Path.Combine(folder, "probe.db"), folder, null, profile: profile);
        var native = await RunAsync(["version"], false, token, probe, captureDisplay: true).ConfigureAwait(false);
        var listing = native.Records.FirstOrDefault(line => line.StartsWith("plugins:", StringComparison.OrdinalIgnoreCase));
        var loaded = listing?["plugins:".Length..].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return native.Succeeded && profile.EnabledPluginIds.All(id => loaded.Contains(id, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public async Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token)
    {
        if (matching is not null)
        {
            var read = await ReadNativeProfileAsync(token).ConfigureAwait(false);
            if (read is not HostOutcome<BeetsNativeConfiguration>.Success { Value: var current })
                return read.PropagateFailure<ImmutableArray<BeetsImportCandidate>>();
            if (current.Settings?.ImportDefaults.GetValueOrDefault(BeetsConfigurationValues.Behavior) != BeetsConfigurationValues.KeepTags)
                return await PreviewMatchesAsync(upload, matching, token).ConfigureAwait(false);
        }
        var staged = await stage.OpenVerifiedAlbumAsync(upload, token).ConfigureAwait(false);
        return staged is HostOutcome<VerifiedImportAlbum>.Success
            ? new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success([])
            : staged.PropagateFailure<ImmutableArray<BeetsImportCandidate>>();
    }

    /// <inheritdoc />
    public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) => work.Choice switch
    {
        ImportChoiceKind.KeepTags => Task.FromResult<HostOutcome<PreparedImportAlbum?>>(new HostOutcome<PreparedImportAlbum?>.Success(null)),
        ImportChoiceKind.UseMatch when matching is not null && options.Matching is not null && work.ApprovedMatch is not null =>
            PrepareMatchAsync(work, matching, options.Matching.FfmpegExecutablePath, token),
        _ => Task.FromResult<HostOutcome<PreparedImportAlbum?>>(new HostOutcome<PreparedImportAlbum?>.Rejected("beets-match-provider-unavailable"))
    };

    /// <inheritdoc />
    public Task<bool> VerifyPreparationAsync(PreparedImportAlbum prepared, CancellationToken token) => VerifyPreparedAlbumAsync(prepared, token);

    /// <inheritdoc />
    public async Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token)
    {
        if (work.ChoiceOperationId is not Guid operation)
            return new HostOutcome<bool>.Rejected("beets-match-provider-unavailable");
        string directory;
        if (work.Choice == ImportChoiceKind.KeepTags)
        {
            var staged = await stage.OpenVerifiedAlbumAsync(work.Upload, token).ConfigureAwait(false);
            if (staged is not HostOutcome<VerifiedImportAlbum>.Success { Value: var album })
                return staged.PropagateFailure<bool>();
            directory = album.Directory;
        }
        else if (work is { Choice: ImportChoiceKind.UseMatch, PreparedMatch: { } prepared } &&
            await VerifyPreparedAlbumAsync(prepared, token).ConfigureAwait(false))
            directory = prepared.Directory;
        else
            return new HostOutcome<bool>.Rejected("beets-prepared-match-unavailable");

        var result = await RunAsync(["import", "-c", "-M", "-W", "-P", "-I", "-A", "-q", "--flat", "--set",
            $"beetsman_operation={operation:N}", "--", directory], captureReadback: false, token).ConfigureAwait(false);
        return new HostOutcome<bool>.Success(result.Started);
    }

    /// <inheritdoc />
    public async Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token)
    {
        if (work.Choice is not (ImportChoiceKind.KeepTags or ImportChoiceKind.UseMatch) || work.ChoiceOperationId is not Guid operation)
            return new HostOutcome<NativeImportObservation>.Rejected("beets-match-provider-unavailable");
        if (!File.Exists(options.CatalogPath))
            return options.CreateCatalogIfMissing && !work.NativeAttempted
                ? new HostOutcome<NativeImportObservation>.Success(new NativeImportObservation.Absent())
                : new HostOutcome<NativeImportObservation>.Unavailable("beets-catalog-unavailable");

        var process = await RunAsync(["list", "-f", Format, $"beetsman_operation::^{operation:N}$"], captureReadback: true, token).ConfigureAwait(false);
        if (!process.Succeeded)
            return new HostOutcome<NativeImportObservation>.Unavailable("beets-readback-unavailable");
        if (process.Records.Length == 0)
            return new HostOutcome<NativeImportObservation>.Success(new NativeImportObservation.Absent());
        try
        {
            var album = await ReadAlbumAsync(work, operation, process.Records, token).ConfigureAwait(false);
            return new HostOutcome<NativeImportObservation>.Success(album is null
                ? new NativeImportObservation.Incomplete() : new NativeImportObservation.Complete(album));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HostOutcome<NativeImportObservation>.Unavailable("beets-copy-readback-unavailable");
        }
    }

    private async Task<ReconciledImportAlbum?> ReadAlbumAsync(GuidedImportWork work, Guid operation, string[] records, CancellationToken token)
    {
        if (records.Length != work.Upload.Files.Length)
            return null;
        var approved = work.Choice == ImportChoiceKind.UseMatch ? work.ApprovedMatch : null;
        if (work.Choice == ImportChoiceKind.UseMatch && (approved is null || work.PreparedMatch?.Tracks.Length != work.Upload.Files.Length))
            return null;
        var digests = work.PreparedMatch is { } prepared ? prepared.Tracks.Select(part => (part.Size, part.Sha256)) :
            work.Upload.Files.Select(part => (part.Size, part.Sha256));
        var pending = digests.Select((part, index) => (part, index)).GroupBy(file => file.part)
            .ToDictionary(group => group.Key, group => new Queue<int>(group.Select(entry => entry.index)));
        var tracks = ImmutableArray.CreateBuilder<ObservedImportTrack>(records.Length);
        NativeTrack? album = null;
        foreach (var line in records)
        {
            if (!TryParseTrack(line, out var track) || !TryGetOwnedPath(musicRoot, track.Path, out var relative))
                return null;
            album ??= track;
            if (album.AlbumId != track.AlbumId || album.Album != track.Album || album.AlbumArtist != track.AlbumArtist)
                return null;
            await using var audio = new FileStream(track.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var size = audio.Length;
            if (!pending.Keys.Any(key => key.Size == size))
                return null;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(audio, token).ConfigureAwait(false));
            if (audio.Position != size || audio.Length != size || !pending.TryGetValue((size, hash), out var indices) || !indices.TryDequeue(out var index))
                return null;
            if (approved is not null && (track.ReleaseId != approved.ReleaseId || track.ReleaseTrackId != approved.Tracks[index].ReleaseTrackId ||
                    track.Title != approved.Tracks[index].Title || track.Artist != approved.Tracks[index].Artist ||
                    track.Album != approved.Album || track.AlbumArtist != approved.AlbumArtist))
                return null;
            var title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(work.Upload.Files[index].Name) : track.Title;
            tracks.Add(new(index, track.TrackId, relative, title, EmptyToNull(track.Artist), track.Number, null, EmptyToNull(track.Genre),
                track.Disc, track.Year));
        }
        var metadata = album!;
        return new(work.Job.LibraryId, work.Job.JobId, work.Upload.UploadId, operation, work.Job.Revision, work.ProviderKey, metadata.AlbumId,
            string.IsNullOrWhiteSpace(metadata.Album) ? "Unknown Album" : metadata.Album, EmptyToNull(metadata.AlbumArtist), tracks.MoveToImmutable());
    }

    private static bool TryGetOwnedPath(string root, string value, out string relative)
    {
        relative = "";
        if (!Path.IsPathFullyQualified(value))
            return false;
        var normalized = Path.GetFullPath(value);
        var path = Path.GetRelativePath(root, normalized);
        if (Path.IsPathRooted(path) || path is "." or ".." || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        var current = root;
        foreach (var segment in path.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        if ((File.GetAttributes(normalized) & FileAttributes.Directory) != 0)
            return false;
        relative = path.Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    /// <summary>Runs native beets with a private configuration and plain UTF-8 readback.</summary>
    /// <remarks>2026-11-26: Keep terminal color escapes out of the exact path previews for FR-BEETS-001#ac-edit and TEST-BEETS-002.</remarks>
    private async Task<ProcessReadback> RunAsync(string[] arguments, bool captureReadback, CancellationToken token, string? preparedConfig = null,
        bool captureDisplay = false)
    {
        token.ThrowIfCancellationRequested();
        string settings;
        try { settings = preparedConfig ?? await PrepareConfigAsync(token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(false, false, []); }
        var workingDirectory = Path.GetDirectoryName(settings)!;
        var start = new ProcessStartInfo(options.ExecutablePath)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.Environment["BEETSDIR"] = workingDirectory;
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["NO_COLOR"] = "1";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(settings);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        return await ExecuteProcessAsync(start, captureReadback ? RecordPrefix + ColumnSeparator : captureDisplay ? "" : null, token).ConfigureAwait(false);
    }

    private static async Task<ProcessReadback> ExecuteProcessAsync(ProcessStartInfo start, string? capturePrefix, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return new(false, false, []);
        }
        catch (Win32Exception) { return new(false, false, []); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(CommandTimeout);
        try
        {
            process.StandardInput.Close();
            var output = ReadRecordsAsync(process.StandardOutput, capturePrefix, limit.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, limit.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(limit.Token)).ConfigureAwait(false);
            var records = await output.ConfigureAwait(false);
            return new(true, process.ExitCode == 0 && records is not null, records ?? []);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(true, false, []);
        }
    }

    private static async Task<string[]?> ReadRecordsAsync(StreamReader reader, string? prefix, CancellationToken token)
    {
        var records = new List<string>();
        var total = 0L;
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (prefix is null || !line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            total += line.Length;
            if (total <= MaxReadbackCharacters)
                records.Add(line);
        }
        return total <= MaxReadbackCharacters ? records.ToArray() : null;
    }

    /// <summary>Loads this operation's revision and produces its private native configuration when needed.</summary>
    /// <remarks>2026-11-25: Isolate each applied configuration revision for FR-BEETS-001#ac-open.</remarks>
    internal async Task<string> PrepareConfigAsync(CancellationToken token)
    {
        var read = await ReadNativeProfileAsync(token).ConfigureAwait(false);
        if (read is not HostOutcome<BeetsNativeConfiguration>.Success { Value: var selected })
            throw new IOException("The committed beets configuration is unavailable.");
        lock (configurationGate)
        {
            if (config is { } cached && cached.Revision == selected.Revision)
                return cached.Path;
            var working = new DirectoryInfo(Path.GetDirectoryName(commandRoot)!);
            if (working.LinkTarget is not null || working.Exists && (working.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The private beets directory is redirected.");
            var scoped = Path.Combine(commandRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scoped);
            var path = WriteBeetsConfig(scoped, options.CatalogPath, musicRoot, options.AlbumPathTemplate, profile: selected.Settings);
            config = (selected.Revision, path);
            return path;
        }
    }

    private async Task<HostOutcome<BeetsNativeConfiguration>> ReadNativeProfileAsync(CancellationToken token)
    {
        if (readProfile is null)
            return new HostOutcome<BeetsNativeConfiguration>.Success(new(0, null));
        var result = await readProfile(token).ConfigureAwait(false);
        if (result is HostOutcome<BeetsNativeConfiguration>.Success { Value.Settings: { } profile } &&
            (profile.CatalogPath != options.CatalogPath || profile.SourceMusicRoot != musicRoot ||
                profile.ConfigurationPath != options.ConfigurationPath || profile.EnabledPluginIds.Any(id =>
                    !options.AllowedPluginIds.Contains(id, StringComparer.Ordinal)) ||
                profile.ImportDefaults.GetValueOrDefault(BeetsConfigurationValues.Behavior) == BeetsConfigurationValues.ReviewSuggestedMatches &&
                    matching is null))
            return new HostOutcome<BeetsNativeConfiguration>.Unavailable("beets-library-configuration-scope-changed");
        return result;
    }

    /// <summary>Writes private native process settings, including explicitly cleared tags for FR-BEETS-001#ac-edit.</summary>
    private static string WriteBeetsConfig(string folder, string database, string destination, string? albumTemplate, Uri? provider = null,
        string[]? clearedFields = null, HostBeetsSettings? profile = null)
    {
        var settings = new Dictionary<string, object>
        {
            ["library"] = database, ["directory"] = destination, ["plugins"] = profile?.EnabledPluginIds.ToArray() ?? [], ["color"] = false,
            ["import"] = new Dictionary<string, object>
            {
                ["copy"] = true, ["move"] = false, ["write"] = false, ["delete"] = false, ["resume"] = false,
                ["incremental"] = false, ["duplicate_action"] = "skip"
            }
        };
        albumTemplate = profile?.NamingTemplate ?? albumTemplate;
        if (!string.IsNullOrEmpty(albumTemplate))
            settings["paths"] = new Dictionary<string, string> { ["default"] = albumTemplate };
        if (provider is not null)
        {
            settings["plugins"] = new[] { "musicbrainz" };
            settings["musicbrainz"] = new Dictionary<string, object> { ["host"] = provider.Authority, ["https"] = provider.Scheme == Uri.UriSchemeHttps };
            // Lookup is limited to one selected release. Keep beets's missing and unmatched track caps.
            settings["match"] = new Dictionary<string, object> { ["strong_rec_thresh"] = 1.01 };
        }
        if (clearedFields is { Length: > 0 })
        {
            settings["plugins"] = new[] { "zero" };
            settings["zero"] = new Dictionary<string, object> { ["fields"] = clearedFields, ["update_database"] = false };
        }
        var path = Path.Combine(folder, "config.yaml");
        File.WriteAllText(path, new SerializerBuilder().Build().Serialize(settings), new UTF8Encoding(false));
        return path;
    }

    private static bool TryParseTrack(string line, out NativeTrack track)
    {
        var part = line.Split(ColumnSeparator);
        if (part.Length == 14 && long.TryParse(part[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 &&
            long.TryParse(part[2], NumberStyles.None, CultureInfo.InvariantCulture, out var album) && album > 0)
        {
            Guid.TryParseExact(part[10], "D", out var release);
            Guid.TryParseExact(part[11], "D", out var releaseTrack);
            var year = PositiveNumber(part[13]);
            track = new(id, album, part[3], part[4], part[5], part[6], part[7], PositiveNumber(part[8]), part[9], release, releaseTrack,
                PositiveNumber(part[12]), year is <= 9999 ? year : null);
            return true;
        }
        track = null!;
        return false;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? PositiveNumber(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private sealed record NativeTrack(long TrackId, long AlbumId, string Path, string Title, string Artist, string Album,
        string AlbumArtist, int? Number, string Genre, Guid ReleaseId, Guid ReleaseTrackId, int? Disc, int? Year);
    private sealed record ProcessReadback(bool Started, bool Succeeded, string[] Records);
}
