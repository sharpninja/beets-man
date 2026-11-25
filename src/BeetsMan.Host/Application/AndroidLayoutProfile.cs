using System.Text;
using System.Text.RegularExpressions;

namespace BeetsMan.Host.Application;

/// <summary>Describes a versioned Android player layout whose paths are relative to one registered share.</summary>
/// <remarks>2026-11-24: Bind the layout contract for FR-FILESYNC-001#ac-layouts, ac-playlist-drop and ac-safety.</remarks>
public sealed partial class AndroidLayoutProfile
{
    /// <summary>Identifies the default layout for new Android exports.</summary>
    public const string DefaultId = "musicolet";

    private static readonly string[] AudioFields = ["albumArtist", "artist", "album", "fileName"];
    private static readonly string[] PlaylistFields = ["playlistName", "playlistFormat"];

    internal AndroidLayoutProfile(string id, string player, long version, string suggestedRoot,
        string audioPathTemplate, string? playlistFormat, string? playlistPathTemplate)
    {
        Id = id;
        Player = player;
        Version = version;
        SuggestedRoot = suggestedRoot;
        AudioPathTemplate = audioPathTemplate;
        PlaylistFormat = playlistFormat;
        PlaylistPathTemplate = playlistPathTemplate;
    }

    /// <summary>Gets the stable profile identity.</summary>
    public string Id { get; }
    /// <summary>Gets the selected player name.</summary>
    public string Player { get; }
    /// <summary>Gets the template version used for revision checks.</summary>
    public long Version { get; }
    /// <summary>Gets the suggested folder on the Android receiver.</summary>
    public string SuggestedRoot { get; }
    /// <summary>Gets the device-relative audio template.</summary>
    public string AudioPathTemplate { get; }
    /// <summary>Gets the registered playlist format, if this layout accepts playlists.</summary>
    public string? PlaylistFormat { get; }
    /// <summary>Gets the device-relative playlist template, if configured.</summary>
    public string? PlaylistPathTemplate { get; }

    /// <summary>Builds one device destination using the album layout and source file name.</summary>
    public string? ResolveAudioPath(string? albumArtist, string? artist, string? album, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('/') || fileName.Contains('\\'))
            return null;
        return Apply(AudioPathTemplate, field => field switch
        {
            "albumArtist" => SafeSegment(string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist, "Unknown Artist"),
            "artist" => SafeSegment(artist, "Unknown Artist"),
            "album" => SafeSegment(album, "Unknown Album"),
            "fileName" => SafeSegment(fileName, "Unknown Track"),
            _ => throw new InvalidOperationException("The layout contains an unrecognized audio field.")
        });
    }

    /// <summary>Builds one device destination for the saved playlist in the configured format.</summary>
    public string? ResolvePlaylistPath(string name) => PlaylistPathTemplate is not { } template ? null :
        Apply(template, field => field == "playlistName" ? SafeSegment(name, "Untitled Playlist") : PlaylistFormat!);

    internal static bool IsAudioTemplate(string template) =>
        template.EndsWith("{fileName}", StringComparison.Ordinal) && IsTemplate(template, AudioFields);

    internal static bool IsPlaylistTemplate(string template, string format) =>
        template.Contains("{playlistName}", StringComparison.Ordinal) && IsTemplate(template, PlaylistFields) &&
        Token().Replace(template, match => match.Groups[1].Value == "playlistFormat" ? format : "value").EndsWith('.' + format, StringComparison.OrdinalIgnoreCase);

    private static bool IsTemplate(string template, string[] fields)
    {
        var sample = Token().Replace(template, match => fields.Contains(match.Groups[1].Value, StringComparer.Ordinal) ? "value" : match.Value);
        return !sample.Contains('{') && !sample.Contains('}') && IsCanonicalRelativePath(sample);
    }

    private static string? Apply(string template, Func<string, string> fieldValue)
    {
        var path = Token().Replace(template, match => fieldValue(match.Groups[1].Value));
        return IsCanonicalRelativePath(path) ? path : null;
    }

    private static string SafeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var clean = new StringBuilder(value.Length);
        foreach (var character in value.Normalize())
            clean.Append(char.IsControl(character) || "<>:\"|?*/\\".Contains(character) ? '_' : character);
        var text = clean.ToString().Trim().TrimEnd(' ', '.');
        if (text.Length == 0)
            return fallback;
        return IsCanonicalRelativePath(text) ? text : "_" + text;
    }

    private static bool IsCanonicalRelativePath(string path) =>
        PlaylistExportPlanner.TryNormalize(path, out var normalized) && path == normalized;

    [GeneratedRegex(@"\{([a-zA-Z]+)\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Token();
}
