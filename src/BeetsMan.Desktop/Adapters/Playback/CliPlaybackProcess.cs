using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Processes;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Owns each one-shot child and at most one newline-delimited JSON session child.</summary>
/// <remarks>2026-11-25: Bind direct argv, request correlation, bounded replies, and process cleanup for FR-CLI-001#ac-switch and TEST-CLI-002.</remarks>
internal sealed class CliPlaybackProcess(CliProfileDraft profile) : IDisposable
{
    private BoundedTextProcess? session;

    /// <summary>Indicates whether the current session can receive another request.</summary>
    internal bool HasSession => session is { HasExited: false };

    /// <summary>Executes the captured action and decodes the matching structured reply.</summary>
    internal async Task<Outcome<Reply>> InvokeAsync(CliActionMapping action, string mediaPath, TimeSpan position, bool allowStart,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(profile.Timeout);
        var cancellation = deadline.Token;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var positionMs = position.Ticks / TimeSpan.TicksPerMillisecond;
            if (profile.Mode == CliInvocationMode.OneShot)
            {
                using var worker = Start(action.Arguments.Select(argument => argument switch
                {
                    CliArgumentToken.MediaPath => mediaPath,
                    CliArgumentToken.PositionMilliseconds => positionMs.ToString(CultureInfo.InvariantCulture),
                    _ => argument
                }));
                worker.CloseInput();
                var line = await worker.ReadAsync(stopAtLine: false, cancellation).ConfigureAwait(false);
                await worker.WaitForExitAsync(cancellation).ConfigureAwait(false);
                return worker.ExitCode == 0 ? Decode(line, null) : Failure("cli-process-exited", true);
            }

            if (!HasSession)
            {
                Dispose();
                if (!allowStart)
                    return Failure("cli-process-exited", true);
                session = Start([]);
            }
            var active = session!;
            var id = Guid.NewGuid();
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, kind = action.RequestKind, path = mediaPath, positionMs });
            await active.WriteAsync(request, cancellation).ConfigureAwait(false);
            var reply = await active.ReadAsync(stopAtLine: true, cancellation).ConfigureAwait(false);
            if (reply is null)
            {
                Dispose();
                return Failure("cli-process-exited", true);
            }
            var outcome = Decode(reply, id);
            if (outcome is not Outcome<Reply>.Success)
                Dispose();
            return outcome;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Dispose();
            return token.IsCancellationRequested ? new Outcome<Reply>.Cancelled() : Failure("cli-process-timeout", true);
        }
        catch (Win32Exception) { Dispose(); return Failure("cli-process-start-failed", true); }
        catch (InvalidOperationException) { Dispose(); return Failure("cli-process-start-failed", true); }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or JsonException)
        { Dispose(); return Failure("invalid-cli-reply", false); }
        catch (IOException)
        {
            var exited = !HasSession && profile.Mode == CliInvocationMode.Session;
            Dispose();
            return Failure(exited ? "cli-process-exited" : "cli-process-io-failed", true);
        }
    }

    /// <summary>Closes the session worker and discards partially read records.</summary>
    public void Dispose()
    {
        session?.Dispose();
        session = null;
    }

    private BoundedTextProcess Start(IEnumerable<string> actionArguments)
    {
        var info = new ProcessStartInfo(profile.Executable);
        foreach (var argument in profile.Arguments.Concat(actionArguments))
            info.ArgumentList.Add(argument);
        return BoundedTextProcess.Start(info);
    }

    private Outcome<Reply> Decode(string? raw, Guid? expectedId)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Failure("invalid-cli-reply", false);
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var mapping = profile.Reply!;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != root.GetPropertyCount() ||
            expectedId is { } id && (!root.TryGetProperty("id", out var correlation) || correlation.ValueKind != JsonValueKind.String ||
                !correlation.TryGetGuid(out var received) || received != id))
            return Failure("invalid-cli-reply", false);
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            return Failure("cli-worker-rejected", false);
        if (!root.TryGetProperty(mapping.StatusField, out var status) || status.ValueKind != JsonValueKind.String ||
            !mapping.StatusValues.TryGetValue(status.GetString()!, out var value) ||
            !TryTime(root, mapping.PositionMillisecondsField, out var position) || !TryTime(root, mapping.DurationMillisecondsField, out var duration) ||
            position > duration)
            return Failure("invalid-cli-reply", false);
        return new Outcome<Reply>.Success(new(value, position, duration));
    }

    private static bool TryTime(JsonElement root, string? name, out TimeSpan? result)
    {
        result = null;
        if (name is null)
            return true;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var milliseconds) ||
            milliseconds < 0 || milliseconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            return false;
        result = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
        return true;
    }

    private static Outcome<Reply> Failure(string code, bool retryable) => new Outcome<Reply>.DependencyFailure(code, retryable);

    internal sealed record Reply(TransportStatus Status, TimeSpan? Position, TimeSpan? Duration);

}
