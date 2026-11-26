using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BeetsMan.Desktop.Adapters.Processes;

/// <summary>Owns one direct child and its bounded text exchange without collecting stderr.</summary>
/// <remarks>2026-11-25: Share process lifetime and UTF-8 framing for TEST-CLI-002 and TEST-PLUGINS-002.</remarks>
/// <remarks>2026-11-26: Bound worker transport and teardown for FR-CLI-001#ac-switch and FR-PLUGINS-001#ac-recovery.</remarks>
internal sealed class BoundedTextProcess : IDisposable
{
    private const int MaximumReplyCharacters = 65_536;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly Process process;
    private readonly Stream input;
    private readonly StreamReader output;
    private readonly Stream errors;
    private readonly char[] buffer = new char[4096];
    private int offset, count;

    private BoundedTextProcess(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput;
        errors = process.StandardError.BaseStream;
        _ = DrainErrorsAsync(errors);
    }

    /// <summary>Starts a process with separate argument tokens and redirected UTF-8 streams.</summary>
    internal static BoundedTextProcess Start(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false, true);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        try { return new BoundedTextProcess(process); }
        catch
        {
            Stop(process);
            process.Dispose();
            throw;
        }
    }

    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal void CloseInput() => input.Dispose();
    internal Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);

    /// <summary>Sends one JSON record and its newline delimiter.</summary>
    internal async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        await input.WriteAsync(bytes, token).ConfigureAwait(false);
        await input.WriteAsync(NewLine, token).ConfigureAwait(false);
    }

    /// <summary>Reads a bounded reply to a newline or EOF, preserving any following line.</summary>
    internal async Task<string?> ReadAsync(bool stopAtLine, CancellationToken token)
    {
        var reply = new StringBuilder();
        while (true)
        {
            if (offset == count)
            {
                count = await output.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                offset = 0;
                if (count == 0)
                    return reply.Length == 0 ? null : stopAtLine ? throw new InvalidDataException() : reply.ToString();
            }
            var newline = stopAtLine ? Array.IndexOf(buffer, '\n', offset, count - offset) : -1;
            var length = (newline < 0 ? count : newline) - offset;
            if (reply.Length + length > MaximumReplyCharacters)
                throw new InvalidDataException();
            reply.Append(buffer, offset, length);
            offset += length;
            if (newline >= 0)
            {
                offset++;
                return reply.ToString();
            }
        }
    }

    public void Dispose()
    {
        Stop(process);
        input.Dispose();
        output.Dispose();
        errors.Dispose();
        process.Dispose();
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    private static async Task DrainErrorsAsync(Stream stream)
    {
        try { await stream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }
}
