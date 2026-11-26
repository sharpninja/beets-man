using System.Collections.Immutable;
using System.Text;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Persists versioned local CLI definitions alongside the desktop's configuration.</summary>
/// <remarks>2026-11-25: Save and load one YAML document per profile for FR-CLI-001#ac-profiles and TR-CLI-ARCH-001.</remarks>
public sealed class YamlCliProfileStore
    : ICliProfileStore
{
    private const int MaximumBytes = 65_536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly IDeserializer Reader = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().WithMaximumRecursion(16).Build();
    private static readonly ISerializer Writer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithEnumNamingConvention(CamelCaseNamingConvention.Instance).DisableAliases().Build();
    private readonly string directory;
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Scopes profile files to a directory next to the selected absolute settings file.</summary>
    public YamlCliProfileStore(string settingsPath)
    {
        if (!Path.IsPathFullyQualified(settingsPath) || Path.GetDirectoryName(settingsPath) is not { } parent)
            throw new ArgumentException("The desktop settings path must be absolute.", nameof(settingsPath));
        directory = Path.Combine(parent, "playback-profiles");
    }

    /// <inheritdoc />
    public Task<Outcome<CliProfileDraft?>> ReadAsync(Guid profileId, CancellationToken token) => OnLaneAsync<CliProfileDraft?>(async () =>
        new Outcome<CliProfileDraft?>.Success(await ReadDocumentAsync(profileId, token).ConfigureAwait(false)), token);

    /// <inheritdoc />
    public Task<Outcome<CliProfileReceipt>> SaveAsync(CliProfileDraft profile, long expectedRevision, CancellationToken token) =>
        OnLaneAsync<CliProfileReceipt>(async () =>
        {
            var previous = await ReadDocumentAsync(profile.ProfileId, token).ConfigureAwait(false);
            var revision = previous?.Revision ?? 0;
            if (revision != expectedRevision)
                return new Outcome<CliProfileReceipt>.Conflict("stale-cli-profile", revision);
            var next = profile with { Revision = checked(revision + 1) };
            var bytes = Utf8.GetBytes(Writer.Serialize(Document.FromDraft(next)));
            if (bytes.Length > MaximumBytes)
                return new Outcome<CliProfileReceipt>.Invalid([new("cli-profile-too-large")]);

            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(temporary, options))
                {
                    await file.WriteAsync(bytes, token).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                var destination = ProfilePath(profile.ProfileId);
                if (previous is not null && OperatingSystem.IsWindows())
                    File.Replace(temporary, destination, destinationBackupFileName: null);
                else
                    File.Move(temporary, destination, overwrite: previous is not null);
            }
            finally { File.Delete(temporary); }
            return new Outcome<CliProfileReceipt>.Success(new(next.ProfileId, next.Revision));
        }, token);

    private string ProfilePath(Guid id) => Path.Combine(directory, $"{id:D}.yaml");

    private async Task<CliProfileDraft?> ReadDocumentAsync(Guid id, CancellationToken token)
    {
        byte[] bytes;
        try
        {
            await using var file = new FileStream(ProfilePath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            if (file.Length is 0 or > MaximumBytes)
                throw new InvalidDataException();
            bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (file.ReadByte() != -1)
                throw new InvalidDataException();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        var yaml = Utf8.GetString(bytes);
        using var source = new StringReader(yaml);
        var parser = new Parser(source);
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias or NodeEvent { Anchor.IsEmpty: false } or NodeEvent { Tag.IsEmpty: false })
                throw new InvalidDataException();
        }

        var data = Reader.Deserialize<Document>(yaml);
        if (data is null || data.ProfileId != id || data.Revision < 1 || !data.TryRead(out var result) || !CliProfileHandlers.Validate(result).Issues.IsEmpty)
            throw new InvalidDataException();
        return result;
    }

    private async Task<Outcome<T>> OnLaneAsync<T>(Func<Task<Outcome<T>>> action, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await action().ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<T>.Cancelled(); }
        catch (Exception exception) when (exception is YamlException or InvalidDataException or DecoderFallbackException or EncoderFallbackException)
        { return new Outcome<T>.DependencyFailure("invalid-cli-profile-file", false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new Outcome<T>.DependencyFailure("cli-profile-file-unavailable", true); }
    }

    private sealed class Document
    {
        public Document() { }
        public Guid ProfileId { get; set; }
        public long Revision { get; set; }
        public int SchemaVersion { get; set; }
        public CliInvocationMode? Mode { get; set; }
        public string? Executable { get; set; }
        public string[]? Arguments { get; set; }
        public string[]? Capabilities { get; set; }
        public long TimeoutMilliseconds { get; set; }
        public ActionDocument[]? Actions { get; set; }
        public ReplyDocument? Reply { get; set; }

        internal bool TryRead(out CliProfileDraft profile)
        {
            profile = null!;
            if (Mode is not { } mode || Executable is null || Arguments is null || Capabilities is null || Actions is null ||
                Actions.Any(action => action?.Arguments is null || action.Action is null) ||
                Reply is not { StatusField: { } status, StatusValues: { } values } reply ||
                TimeoutMilliseconds is < 1 or > 300_000)
                return false;
            var flags = PlaybackCapabilities.None;
            foreach (var name in Capabilities)
            {
                if (Enum.GetNames<PlaybackCapabilities>().Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) &&
                    Enum.TryParse<PlaybackCapabilities>(name, ignoreCase: true, out var flag))
                    flags |= flag;
            }
            profile = new(ProfileId, Revision, SchemaVersion, mode, Executable, [.. Arguments], flags,
                TimeSpan.FromMilliseconds(TimeoutMilliseconds),
                [.. Actions.Select(action => new CliActionMapping(action.Action!.Value, [.. action.Arguments!], action.RequestKind))],
                new(status, values.ToImmutableDictionary(StringComparer.Ordinal), reply.PositionMillisecondsField, reply.DurationMillisecondsField));
            return true;
        }

        internal static Document FromDraft(CliProfileDraft draft) => new()
        {
            ProfileId = draft.ProfileId, Revision = draft.Revision, SchemaVersion = draft.SchemaVersion, Mode = draft.Mode,
            Executable = draft.Executable, Arguments = [.. draft.Arguments],
            Capabilities = [.. Enum.GetValues<PlaybackCapabilities>().Where(flag => flag != PlaybackCapabilities.None && draft.Capabilities.HasFlag(flag))
                .Select(flag => CamelCaseNamingConvention.Instance.Apply(flag.ToString()))],
            TimeoutMilliseconds = draft.Timeout.Ticks / TimeSpan.TicksPerMillisecond,
            Actions = draft.Actions.IsDefault ? [] : [.. draft.Actions.Select(action => new ActionDocument
                { Action = action.Action, Arguments = [.. action.Arguments], RequestKind = action.RequestKind })],
            Reply = new()
            {
                StatusField = draft.Reply!.StatusField, StatusValues = new(draft.Reply.StatusValues, StringComparer.Ordinal),
                PositionMillisecondsField = draft.Reply.PositionMillisecondsField, DurationMillisecondsField = draft.Reply.DurationMillisecondsField
            }
        };
    }

    private sealed class ActionDocument
    {
        public ActionDocument() { }
        public CliPlaybackAction? Action { get; set; }
        public string[]? Arguments { get; set; }
        public string? RequestKind { get; set; }
    }

    private sealed class ReplyDocument
    {
        public ReplyDocument() { }
        public string? StatusField { get; set; }
        public Dictionary<string, TransportStatus>? StatusValues { get; set; }
        public string? PositionMillisecondsField { get; set; }
        public string? DurationMillisecondsField { get; set; }
    }
}
