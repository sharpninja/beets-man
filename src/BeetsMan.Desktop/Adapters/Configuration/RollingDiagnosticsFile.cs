using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeetsMan.Desktop.Application.Configuration;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Stores bounded redacted JSON lines in date and size based rolling segments.</summary>
/// <remarks>2026-11-24: Persist structured logs and enforce retention for FR-CONFIG-001#ac-defaults and ac-logs.</remarks>
internal sealed partial class RollingDiagnosticsFile(RollingLogSettings settings)
{
    private const int SegmentBytes = 1024 * 1024;
    private string? segment;
    private DateOnly day;
    private int sequence;
    private long segmentLength;

    /// <summary>Appends an entry from the provider's single writer.</summary>
    public void Append(DiagnosticEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        var newDay = DateOnly.FromDateTime(entry.OccurredAt.UtcDateTime);
        try
        {
            if (segment is null || newDay != day || segmentLength + bytes.Length + 1 > SegmentBytes)
                SelectSegment(newDay, bytes.Length + 1);

            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(segment!, options);
            file.Write(bytes);
            file.WriteByte((byte)'\n');
            segmentLength += bytes.Length + 1;
        }
        catch (IOException) { segment = null; }
        catch (UnauthorizedAccessException) { segment = null; }
    }

    private void SelectSegment(DateOnly currentDay, int entrySize)
    {
        Directory.CreateDirectory(settings.Directory);
        day = currentDay;
        var prefix = "beets-man-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var owned = Directory.EnumerateFiles(settings.Directory, "beets-man-*.jsonl")
            .Where(path => SegmentName().IsMatch(Path.GetFileName(path))).ToList();
        var latest = owned.Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            .OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        sequence = latest is null ? 0 : int.Parse(Path.GetFileNameWithoutExtension(latest).AsSpan(prefix.Length), CultureInfo.InvariantCulture);
        segmentLength = latest is null ? 0 : new FileInfo(latest).Length;
        if (segmentLength + entrySize > SegmentBytes)
        {
            sequence++;
            segmentLength = 0;
        }
        segment = Path.Combine(settings.Directory, prefix + sequence.ToString("D8", CultureInfo.InvariantCulture) + ".jsonl");
        foreach (var expired in owned.Where(path => path != segment).OrderDescending(StringComparer.Ordinal).Skip(settings.RetainedFileCount - 1))
            File.Delete(expired);
    }

    [GeneratedRegex("^beets-man-[0-9]{8}-[0-9]{8}\\.jsonl$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentName();
}
