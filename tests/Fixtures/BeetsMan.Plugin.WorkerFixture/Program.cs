using System.Text.Json;

// 2026-11-25: Exercise the host's actual message reader with independent replies for TEST-PLUGINS-002.
var mode = (await File.ReadAllTextAsync("mode.txt")).Trim();
var raw = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("Missing greeting.");
using var greeting = JsonDocument.Parse(raw);
var hello = greeting.RootElement;
if (hello.GetProperty("protocol").GetInt32() != 1 || hello.GetProperty("kind").GetString() != "hello" ||
    hello.GetProperty("pluginId").GetString() != "example.visualizer" || hello.GetProperty("version").GetString() != "1.0.0")
    return 11;
if (mode == "exit")
    return 17;
if (mode == "timeout")
    await Task.Delay(TimeSpan.FromSeconds(30));
if (mode == "report-pid")
    await File.WriteAllTextAsync("worker.pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

switch (mode)
{
    case "malformed":
        Console.WriteLine("{broken");
        break;
    case "oversized":
        Console.WriteLine(new string('x', 70_000));
        break;
    default:
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            protocol = 1, kind = "ready",
            requestId = mode == "wrong-id" ? Guid.NewGuid() : hello.GetProperty("requestId").GetGuid(),
            pluginId = "example.visualizer", version = mode == "wrong-version" ? "2.0.0" : "1.0.0",
            contributions = mode switch
            {
                "actions" => new[] { new { contributionId = "pause", kind = "action" }, new { contributionId = "note", kind = "action" } },
                "panel" => new[] { new { contributionId = "listening-notes", kind = "panel" }, new { contributionId = "save-note", kind = "action" } },
                "panel-no-action" => new[] { new { contributionId = "listening-notes", kind = "panel" } },
                "visualization" => new object[] { new { contributionId = "levels", kind = "visualization", telemetryProtocol = 1 } },
                _ => new[] { new { contributionId = mode == "undeclared" ? "unknown" : "levels", kind = "visualization" } }
            }
        }));
        break;
}
if (mode == "exit-after-ready")
    await Task.Delay(TimeSpan.FromMilliseconds(600));
else if (mode == "actions")
{
    while (await Console.In.ReadLineAsync() is { } actionLine)
    {
        using var document = JsonDocument.Parse(actionLine);
        var call = document.RootElement;
        var name = call.GetProperty("contributionId").GetString();
        if (call.GetProperty("kind").GetString() != "invoke" || call.GetProperty("operationId").GetGuid() == Guid.Empty)
            return 21;
        var input = call.GetProperty("arguments").GetProperty("input").GetString();
        if (input == "timeout")
            await Task.Delay(TimeSpan.FromSeconds(30));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            protocol = 1, kind = "result", requestId = call.GetProperty("requestId").GetGuid(),
            pluginId = "example.visualizer", version = "1.0.0", contributionId = name,
            message = input == "literal note ; $(example)" ? "Paused by approved action" : "Worker result",
            hostAction = input == "unauthorized" ? "settings.write" : "playback.pause"
        }));
    }
}
else if (mode == "panel")
{
    // 2026-11-25: Capture the actual worker input for FR-PLUGINS-001#ac-visualize.
    while (await Console.In.ReadLineAsync() is { } actionLine)
    {
        using var document = JsonDocument.Parse(actionLine);
        var call = document.RootElement;
        if (call.GetProperty("kind").GetString() != "invoke" || call.GetProperty("operationId").GetGuid() == Guid.Empty ||
            call.GetProperty("contributionId").GetString() != "save-note")
            return 22;
        await File.WriteAllTextAsync("panel-arguments.json", call.GetProperty("arguments").GetRawText());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            protocol = 1, kind = "result", requestId = call.GetProperty("requestId").GetGuid(),
            pluginId = "example.visualizer", version = "1.0.0", contributionId = "save-note",
            message = "Listening note saved"
        }));
    }
}
else if (mode == "visualization")
{
    // 2026-11-25: Validate real messages and reply with current, stale, and timed-out frames for TEST-PLUGINS-002.
    var count = 0;
    while (await Console.In.ReadLineAsync() is { } frameLine)
    {
        using var document = JsonDocument.Parse(frameLine);
        var call = document.RootElement;
        var sequence = call.GetProperty("sequence").GetInt64();
        var waveform = call.GetProperty("waveform");
        var spectrum = call.GetProperty("spectrum");
        if (call.GetProperty("kind").GetString() != "visualize" || call.GetProperty("protocol").GetInt32() != 1 ||
            call.GetProperty("contributionId").GetString() != "levels" || call.GetProperty("pluginId").GetString() != "example.visualizer" ||
            call.GetProperty("version").GetString() != "1.0.0" || call.GetProperty("sampleRate").GetInt32() != 48000 ||
            waveform.GetArrayLength() != 64 || waveform[0].GetSingle() != -0.25f ||
            spectrum.GetArrayLength() != 32 || spectrum[0].GetSingle() != 0.375f)
            return 23;
        count++;
        await File.WriteAllTextAsync($"visualization-{count}.json", frameLine);
        if (count == 3)
            await Task.Delay(TimeSpan.FromSeconds(30));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            protocol = 1, kind = "frame", requestId = call.GetProperty("requestId").GetGuid(),
            pluginId = "example.visualizer", version = "1.0.0", contributionId = "levels",
            sessionId = call.GetProperty("sessionId").GetGuid(),
            sequence = count == 2 ? sequence - 1 : sequence,
            levels = count == 2 ? new[] { 0.98f } : new[] { 0.125f, 0.5f, 0.875f }
        }));
    }
}
else
    await Console.In.ReadLineAsync();
return 0;

namespace BeetsMan.Plugin.WorkerFixture
{
    /// <summary>Locates the real test executable and its .NET hosting metadata.</summary>
    public sealed class WorkerFixtureAssembly;
}
