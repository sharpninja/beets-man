using System.Collections.Immutable;
using System.Globalization;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Identifies the supported contexts and the editor's aggregate scope.</summary>
/// <remarks>2026-11-25: Define STB17 contextual ownership for FR-SETTINGS-001#ac-shortcuts.</remarks>
public static class ShortcutScopes
{
    public const string All = "all";
    public const string Player = "player";
    public const string Library = "library";
    public const string Dialog = "dialog";
    public const string System = "system";

    internal static bool IsContext(string? scope) => scope is Player or Library or Dialog;
}

/// <summary>Defines the supported shortcut actions and stage 0 defaults.</summary>
/// <remarks>2026-11-25: Bind FR-SETTINGS-001#ac-shortcuts to the approved STB17 gestures.</remarks>
public static class ShortcutCatalog
{
    public const string PlayPause = "playback.play-pause";
    public const string ToggleAutoFade = "playback.toggle-auto-fade";
    public const string SearchLibrary = "library.search";
    public const string CloseDialog = "shell.close-dialog";
    public const string SkipNext = "playback.next";
    public const string SkipPrevious = "playback.previous";
    public const string Stop = "playback.stop";

    public static ImmutableArray<ShortcutBinding> DefaultSystemBindings { get; } = [new(PlayPause, ShortcutScopes.System, "MediaPlayPause")];

    internal static ImmutableArray<ShortcutBinding> Defaults { get; } =
        [new(PlayPause, ShortcutScopes.Player, "Space"), new(SearchLibrary, ShortcutScopes.Library, "Ctrl+F"),
            new(CloseDialog, ShortcutScopes.Dialog, "Esc")];

    private static readonly string[] Modifiers = ["Ctrl", "Alt", "Shift", "Meta"];
    private static readonly string[] NamedKeys = ["Space", "Esc", "Enter", "Tab", "Backspace", "Delete", "Insert",
        "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right", "Plus", "Minus", "Comma", "Period",
        "MediaPlayPause", "MediaNextTrack", "MediaPreviousTrack", "MediaStop"];

    /// <summary>Checks an action's allowed local scopes before an assignment is admitted.</summary>
    public static bool AllowedInContext(string? action, string? scope) => action switch
    {
        PlayPause or ToggleAutoFade or SkipNext or SkipPrevious or Stop => scope == ShortcutScopes.Player,
        SearchLibrary => scope is ShortcutScopes.Library or ShortcutScopes.Player,
        CloseDialog => scope == ShortcutScopes.Dialog,
        _ => false
    };

    internal static bool AllowedGlobally(string? action) => action is PlayPause or SkipNext or SkipPrevious or Stop;

    /// <summary>Normalizes captured keys and modifier order without involving a platform input API.</summary>
    public static string? NormalizeGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || gesture.Length > 80)
            return null;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length > 5 || NormalizeKey(parts[^1]) is not { } key)
            return null;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts.AsSpan(0, parts.Length - 1))
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => "Ctrl",
                "ALT" => "Alt",
                "SHIFT" => "Shift",
                "META" or "SUPER" or "CMD" => "Meta",
                _ => null
            };
            if (modifier is null || !flags.Add(modifier))
                return null;
        }
        return string.Join('+', Modifiers.Where(flags.Contains).Append(key));
    }

    private static string? NormalizeKey(string raw)
    {
        var key = raw.ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            return key;
        if (key.Length is 2 or 3 && key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
            return "F" + function.ToString(CultureInfo.InvariantCulture);
        return key switch
        {
            "ESCAPE" => "Esc",
            "RETURN" => "Enter",
            "MEDIA PLAY/PAUSE" => "MediaPlayPause",
            _ => NamedKeys.FirstOrDefault(known => string.Equals(known, raw, StringComparison.OrdinalIgnoreCase))
        };
    }
}
