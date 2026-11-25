using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Persists the typed bootstrap settings while retaining unrelated JSON keys.</summary>
/// <remarks>2026-11-24: Implement the per-user appsettings behavior for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed class JsonBootstrapFile(BootstrapSettings defaults) : IBootstrapFile
{
    private const string Missing = "missing";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonNodeOptions NameOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public BootstrapSettings Defaults { get; } = defaults;

    /// <summary>Resolves the built-in application and Music path variables to supplied absolute locations.</summary>
    public static BootstrapSettings ResolveDefaults(string appData, string musicRoot)
    {
        if (!Path.IsPathFullyQualified(appData) || !Path.IsPathFullyQualified(musicRoot))
            throw new ArgumentException("Bootstrap default roots must be absolute.");
        var levels = ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase)
            .Add("Default", "Information").Add("Microsoft", "Warning");
        return new(Path.Combine(appData, "appsettings.json"), Path.Combine(appData, "data", "beets-man.db"), musicRoot,
            levels, new(true, Path.Combine(appData, "logs"), 14));
    }

    /// <inheritdoc />
    public async Task<BootstrapFileRead> ReadAsync(CancellationToken cancellationToken) =>
        (await ReadDocumentAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    /// <inheritdoc />
    public async Task<Outcome<BootstrapFileRead>> WriteAsync(BootstrapSettings settings, string expectedStamp, CancellationToken cancellationToken)
    {
        var current = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (current.Snapshot.Stamp != expectedStamp)
            return new Outcome<BootstrapFileRead>.Conflict("configuration-changed", null);
        if (!current.Snapshot.Issues.IsDefaultOrEmpty)
            return new Outcome<BootstrapFileRead>.Invalid(current.Snapshot.Issues);

        var root = current.Root ?? new JsonObject(NameOptions);
        var logging = EnsureObject(root, "Logging");
        var levels = EnsureObject(logging, "LogLevel");
        foreach (var setting in settings.LogLevels)
            levels[setting.Key] = setting.Value;

        if (settings.RollingLog is { } rolling)
        {
            var file = EnsureObject(logging, "RollingFile");
            file["Enabled"] = rolling.Enabled;
            file["Directory"] = rolling.Directory;
            file["RetainedFileCount"] = rolling.RetainedFileCount;
        }
        EnsureObject(root, "Storage")["LiteDbPath"] = settings.DatabasePath;
        EnsureObject(root, "Library")["LocalMusicRoot"] = settings.MusicRoot;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, WriteOptions);
        var destination = Defaults.SettingsPath;
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".beets-appsettings-{Guid.NewGuid():N}.tmp");

        try
        {
            var outputOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                outputOptions.UnixCreateMode = current.Snapshot.Exists ? File.GetUnixFileMode(destination) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temp, outputOptions))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var latest = (await ReadDocumentAsync(cancellationToken).ConfigureAwait(false)).Snapshot;
            if (latest.Stamp != expectedStamp)
                return new Outcome<BootstrapFileRead>.Conflict("configuration-changed", null);
            cancellationToken.ThrowIfCancellationRequested();
            if (latest.Exists && OperatingSystem.IsWindows())
                File.Replace(temp, destination, destinationBackupFileName: null);
            else
                File.Move(temp, destination, overwrite: latest.Exists);
            return new Outcome<BootstrapFileRead>.Success(new(Stamp(bytes), true, settings, false, []));
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private async Task<Document> ReadDocumentAsync(CancellationToken token)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(Defaults.SettingsPath, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return new(null, new(Missing, false, null, true, [])); }
        catch (DirectoryNotFoundException) { return new(null, new(Missing, false, null, true, [])); }

        var stamp = Stamp(bytes);
        try
        {
            if (JsonNode.Parse(bytes, NameOptions, ParseOptions) is not JsonObject root)
                return Broken(stamp);
            return new(root, ReadSettings(root, stamp));
        }
        catch (JsonException) { return Broken(stamp); }
        catch (InvalidOperationException) { return Broken(stamp); }
        catch (FormatException) { return Broken(stamp); }
    }

    private BootstrapFileRead ReadSettings(JsonObject root, string stamp)
    {
        bool incomplete = false;
        T Read<T>(JsonObject? parent, string key, T fallback)
        {
            if (parent is null || !parent.TryGetPropertyValue(key, out var value))
            {
                incomplete = true;
                return fallback;
            }
            return value is JsonValue scalar ? scalar.GetValue<T>() : throw new FormatException("Expected a scalar setting.");
        }

        var logging = ReadObject(root, "Logging");
        var configuredLevels = ReadObject(logging, "LogLevel");
        var levels = Defaults.LogLevels.ToBuilder();
        foreach (var setting in Defaults.LogLevels)
            if (configuredLevels?.ContainsKey(setting.Key) != true)
                incomplete = true;
        if (configuredLevels is not null)
        {
            foreach (var setting in configuredLevels)
                levels[setting.Key] = setting.Value is JsonValue scalar ? scalar.GetValue<string>() : throw new FormatException("Expected a log level.");
        }

        var rolling = Defaults.RollingLog;
        if (rolling is not null)
        {
            var configuredFile = ReadObject(logging, "RollingFile");
            rolling = new(Read(configuredFile, "Enabled", rolling.Enabled), Read(configuredFile, "Directory", rolling.Directory),
                Read(configuredFile, "RetainedFileCount", rolling.RetainedFileCount));
        }
        var settings = new BootstrapSettings(Defaults.SettingsPath, Read(ReadObject(root, "Storage"), "LiteDbPath", Defaults.DatabasePath),
            Read(ReadObject(root, "Library"), "LocalMusicRoot", Defaults.MusicRoot), levels.ToImmutable(), rolling);
        return new(stamp, true, settings, incomplete, []);
    }

    private static JsonObject? ReadObject(JsonObject? owner, string key)
    {
        if (owner is null || !owner.TryGetPropertyValue(key, out var value))
            return null;
        return value as JsonObject ?? throw new FormatException("Expected a settings section.");
    }

    private static JsonObject EnsureObject(JsonObject owner, string key)
    {
        if (ReadObject(owner, key) is { } current)
            return current;
        var created = new JsonObject(NameOptions);
        owner[key] = created;
        return created;
    }

    private static Document Broken(string stamp) => new(null,
        new(stamp, true, null, false, [new("malformed-configuration", nameof(BootstrapSettings.SettingsPath))]));

    private static string Stamp(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Document(JsonObject? Root, BootstrapFileRead Snapshot);
}
