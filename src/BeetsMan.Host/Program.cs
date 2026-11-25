using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Bootstrap;
using Microsoft.Extensions.Hosting;

namespace BeetsMan.Host;

/// <summary>Runs the standalone library host with user-local settings and credential storage.</summary>
/// <remarks>2026-11-24: Expose the pre-UI private host for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
internal static class Program
{
    /// <summary>Runs the configured HTTPS service or returns a nonzero startup status.</summary>
    public static async Task<int> Main(string[] args)
    {
        const string usage = "Usage: BeetsMan.Host --config <host-settings.json>";
        if (args is ["--help"])
        {
            Console.WriteLine(usage);
            return 0;
        }
        if (args is not ["--config", var path])
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException();
            var settings = await LibraryHostLauncher.ReadSettingsAsync(path, CancellationToken.None).ConfigureAwait(false);
            using var certificate = LibraryHostLauncher.ReadCertificate(settings.Listener);
            await using var host = await LibraryHostLauncher.BuildAsync(settings, certificate, new OperatingSystemHostCredentialVault(),
                CancellationToken.None).ConfigureAwait(false);
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or IOException or UnauthorizedAccessException or
            CryptographicException or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Library host startup failed. Check the configuration, configured paths, TLS certificate and local credential references.");
            return 1;
        }
    }
}
