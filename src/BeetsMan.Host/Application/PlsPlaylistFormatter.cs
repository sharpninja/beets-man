using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace BeetsMan.Host.Application;

/// <summary>Formats ordered relative paths in the PLS v2 format accepted by Poweramp.</summary>
/// <remarks>2026-11-24: Support a second registered playlist format for FR-FILESYNC-001#ac-playlist-drop.</remarks>
public sealed class PlsPlaylistFormatter : IExportPlaylistFormatter
{
    /// <inheritdoc />
    public string Format => "pls";

    /// <inheritdoc />
    public ImmutableArray<byte> FormatTracks(ImmutableArray<string> orderedRelativeTracks)
    {
        var result = new StringBuilder("[playlist]\n");
        for (var index = 0; index < orderedRelativeTracks.Length; index++)
        {
            result.Append("File").Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append('=').Append(orderedRelativeTracks[index]).Append('\n');
        }
        result.Append("NumberOfEntries=").Append(orderedRelativeTracks.Length.ToString(CultureInfo.InvariantCulture)).Append("\nVersion=2\n");
        return [.. Encoding.UTF8.GetBytes(result.ToString())];
    }
}
