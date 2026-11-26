using System.Diagnostics;

namespace BeetsMan.Desktop.Adapters.Configuration;

/// <summary>Hands the activated log directory to the operating system's file manager.</summary>
/// <remarks>2026-11-25: Provide the SCR03 handoff boundary for FR-CONFIG-001#ac-logs.</remarks>
public interface ILogFolderBrowser
{
    /// <summary>Opens an existing folder or creates it when rolling logging is enabled.</summary>
    Task<bool> OpenAsync(string absoluteDirectory, bool createIfMissing, CancellationToken token);
}

/// <summary>Uses the platform association for a validated absolute directory.</summary>
/// <remarks>2026-11-25: Hand off to the file manager outside the presentation thread for FR-CONFIG-001#ac-logs.</remarks>
internal sealed class ShellLogFolderBrowser(Func<ProcessStartInfo, bool>? launch = null) : ILogFolderBrowser
{
    /// <inheritdoc />
    public Task<bool> OpenAsync(string absoluteDirectory, bool createIfMissing, CancellationToken token) => Task.Run(() =>
    {
        if (!Path.IsPathFullyQualified(absoluteDirectory))
            return false;
        if (createIfMissing)
            Directory.CreateDirectory(absoluteDirectory);
        else if (!Directory.Exists(absoluteDirectory))
            return false;
        token.ThrowIfCancellationRequested();
        return (launch ?? Start)(new ProcessStartInfo { FileName = absoluteDirectory, UseShellExecute = true });
    }, token);

    private static bool Start(ProcessStartInfo request)
    {
        // A successful shell handoff can reuse an existing file manager without returning a process.
        using var process = Process.Start(request);
        return true;
    }
}
