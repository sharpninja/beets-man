using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Loads reference-only host configuration and constructs the explicit HTTPS listener.</summary>
/// <remarks>2026-11-24: Authenticate registered instances over HTTPS for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
public static class LibraryHostLauncher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    /// <summary>Reads the specified JSON document without modifying it.</summary>
    public static async Task<LibraryHostLaunchSettings> ReadSettingsAsync(string path, CancellationToken token)
    {
        await using var source = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LibraryHostLaunchSettings>(source, JsonOptions, token).ConfigureAwait(false)
            ?? throw new ArgumentException("Host settings are required.", nameof(path));
    }

    /// <summary>Returns a caller-owned certificate from the current user's personal certificate store.</summary>
    public static X509Certificate2 ReadCertificate(LibraryHostListener listener)
    {
        var thumbprint = NormalizeThumbprint(listener.CertificateThumbprint);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var candidates = store.Certificates;
        X509Certificate2? selected = null;
        try
        {
            selected = candidates.FirstOrDefault(certificate => Matches(certificate, thumbprint))
                ?? throw new InvalidOperationException("The configured TLS certificate and private key are unavailable.");
            return selected;
        }
        finally
        {
            foreach (var certificate in candidates)
                if (certificate != selected)
                    certificate.Dispose();
        }
    }

    /// <summary>Composes the standalone host with the supplied vault and caller-owned TLS certificate.</summary>
    public static async Task<WebApplication> BuildAsync(LibraryHostLaunchSettings settings, X509Certificate2 certificate,
        ILocalHostCredentialVault vault, CancellationToken token)
    {
        var listener = settings.Listener;
        if (!IPAddress.TryParse(listener.Address, out var address) || listener.Port is < 0 or > 65535)
            throw new ArgumentException("Set a valid IP address and HTTPS port.", nameof(settings));
        if (!Matches(certificate, NormalizeThumbprint(listener.CertificateThumbprint)))
            throw new ArgumentException("The TLS certificate must match the configured thumbprint and have a valid private key.", nameof(certificate));
        var credentials = await LibraryHostCredentials.LoadAsync(settings.Host, settings.Credentials, vault, token).ConfigureAwait(false);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(address, listener.Port, endpoint => endpoint.UseHttps(certificate)));
        return LibraryHostStartup.Build(builder, settings.Host, credentials, credentials, credentials);
    }

    private static string NormalizeThumbprint(string value)
    {
        var trimmed = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        return trimmed.Length == 40 && trimmed.All(char.IsAsciiHexDigit)
            ? trimmed
            : throw new ArgumentException("A valid certificate thumbprint is required.");
    }

    private static bool Matches(X509Certificate2 certificate, string thumbprint)
    {
        var now = DateTime.UtcNow;
        return string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) && certificate.HasPrivateKey &&
            certificate.NotBefore.ToUniversalTime() <= now && now <= certificate.NotAfter.ToUniversalTime();
    }
}
