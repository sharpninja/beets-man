using System.Collections.Immutable;
using System.Globalization;
using BeetsMan.Host.Application;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace BeetsMan.Host.Adapters.Syncthing;

/// <summary>Loads and validates operator-provided or bundled Android layout profiles.</summary>
/// <remarks>2026-11-24: Read versioned YAML profiles without executing input for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed class AndroidLayoutProfiles(IEnumerable<IExportPlaylistFormatter> formatters)
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "id", "player", "templateVersion", "suggestedRoot", "audioPathTemplate", "playlistFormat", "playlistPathTemplate"
    };
    private readonly ImmutableHashSet<string> formats = formatters.Select(formatter => formatter.Format).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identifies the default layout used for a new Android registration.</summary>
    public const string DefaultId = AndroidLayoutProfile.DefaultId;

    /// <summary>Returns the validated Musicolet and Poweramp profiles bundled with the host.</summary>
    public ImmutableArray<AndroidLayoutProfile> LoadBuiltIn() => [LoadResource(DefaultId), LoadResource("poweramp")];

    /// <summary>Parses one layout and rejects invalid paths, unsupported formats, and ambiguous YAML.</summary>
    public HostOutcome<AndroidLayoutProfile> Parse(string yaml)
    {
        if (yaml.Length is 0 or > 16384)
            return Invalid("invalid-device-layout-yaml");

        Dictionary<string, string> document;
        try
        {
            using var reader = new StringReader(yaml);
            var parser = new Parser(reader);
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            var mapping = parser.Consume<MappingStart>();
            if (!mapping.Anchor.IsEmpty || !mapping.Tag.IsEmpty)
                return Invalid("invalid-device-layout-yaml");
            document = new(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>();
                var value = parser.Consume<Scalar>();
                if (!key.Anchor.IsEmpty || !key.Tag.IsEmpty || !value.Anchor.IsEmpty || !value.Tag.IsEmpty ||
                    !Fields.Contains(key.Value) || !document.TryAdd(key.Value, value.Value))
                    return Invalid("invalid-device-layout-yaml");
            }
            parser.Consume<DocumentEnd>();
            parser.Consume<StreamEnd>();
        }
        catch (YamlException) { return Invalid("invalid-device-layout-yaml"); }

        if (!document.TryGetValue("id", out var id) || id.Length is 0 or > 64 || !char.IsAsciiLetterOrDigit(id[0]) ||
            !id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
            !document.TryGetValue("player", out var player) || string.IsNullOrWhiteSpace(player) || player.Any(char.IsControl) ||
            !long.TryParse(document.GetValueOrDefault("templateVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0)
            return Invalid("invalid-device-layout-metadata");
        var root = document.GetValueOrDefault("suggestedRoot");
        var audio = document.GetValueOrDefault("audioPathTemplate");
        var format = document.GetValueOrDefault("playlistFormat")?.ToLowerInvariant();
        var playlist = document.GetValueOrDefault("playlistPathTemplate");
        if (root is not { Length: > 1 } || root[0] != '/' || !PlaylistExportPlanner.TryNormalize(root[1..], out var normalized) || root[1..] != normalized ||
            audio is null || !AndroidLayoutProfile.IsAudioTemplate(audio))
            return Invalid("invalid-device-layout-path");
        if (format is not null && !formats.Contains(format))
            return Invalid("unsupported-playlist-format");
        if ((format is null) != (playlist is null) || format is not null && !AndroidLayoutProfile.IsPlaylistTemplate(playlist!, format))
            return Invalid("invalid-device-layout-path");
        return new HostOutcome<AndroidLayoutProfile>.Success(new(id, player, revision, root, audio, format, playlist));
    }

    private AndroidLayoutProfile LoadResource(string id)
    {
        var assembly = typeof(AndroidLayoutProfiles).Assembly;
        using var stream = assembly.GetManifestResourceStream($"BeetsMan.Host.AndroidLayouts.{id}.yaml") ??
            throw new InvalidOperationException($"The bundled Android layout '{id}' is missing.");
        using var reader = new StreamReader(stream);
        if (Parse(reader.ReadToEnd()) is not HostOutcome<AndroidLayoutProfile>.Success { Value: var profile } || profile.Id != id)
            throw new InvalidOperationException($"The bundled Android layout '{id}' is invalid.");
        return profile;
    }

    private static HostOutcome<AndroidLayoutProfile> Invalid(string code) => new HostOutcome<AndroidLayoutProfile>.Rejected(code);
}
