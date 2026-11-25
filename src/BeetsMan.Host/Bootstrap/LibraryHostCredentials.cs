using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Api;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Resolves configured credentials into the host's independent identity and Syncthing services.</summary>
/// <remarks>2026-11-24: Keep tokens in the OS store and authenticate their exact instance for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
public sealed class LibraryHostCredentials : IHostAccessTokenVerifier, IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
{
    private readonly Guid libraryId;
    private readonly ILocalHostCredentialVault vault;
    private readonly FrozenDictionary<string, Guid> clientFingerprints;
    private readonly FrozenDictionary<Guid, string> receivers;
    private readonly FrozenDictionary<string, string> senders;

    private LibraryHostCredentials(Guid libraryId, ILocalHostCredentialVault vault, Dictionary<string, Guid> clients,
        Dictionary<Guid, string> receivers, Dictionary<string, string> senders)
    {
        this.libraryId = libraryId;
        this.vault = vault;
        clientFingerprints = clients.ToFrozenDictionary(StringComparer.Ordinal);
        this.receivers = receivers.ToFrozenDictionary();
        this.senders = senders.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Loads the configured client fingerprints and validates that each configured share has credential references.</summary>
    public static async Task<LibraryHostCredentials> LoadAsync(LibraryHostOptions host, HostCredentialReferences configured,
        ILocalHostCredentialVault vault, CancellationToken token)
    {
        var clients = new Dictionary<Guid, string>();
        var receivers = new Dictionary<Guid, string>();
        var senders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var client in configured.Clients.IsDefault ? [] : configured.Clients)
        {
            if (client is null)
                throw new ArgumentException("A desktop credential reference is missing.");
            AddReference(clients, client.InstanceId, client.Reference);
        }
        foreach (var receiver in configured.Receivers.IsDefault ? [] : configured.Receivers)
        {
            if (receiver is null)
                throw new ArgumentException("A receiver credential reference is missing.");
            AddReference(receivers, receiver.DeviceId, receiver.Reference);
        }
        foreach (var sender in configured.Senders.IsDefault ? [] : configured.Senders)
        {
            if (sender is null || string.IsNullOrWhiteSpace(sender.SyncthingDeviceId) || !IsReference(sender.Reference) ||
                !senders.TryAdd(sender.SyncthingDeviceId, sender.Reference))
                throw new ArgumentException("Host sender credential references must be valid and unique.");
        }
        foreach (var export in host.AndroidExports.IsDefault ? [] : host.AndroidExports)
        {
            if (export?.Sender is not { } sender || !receivers.ContainsKey(export.DeviceId) || !senders.ContainsKey(sender.DeviceId))
                throw new ArgumentException("Each configured export needs sender and receiver credential references.");
        }
        foreach (var mirror in host.PcMirrors.IsDefault ? [] : host.PcMirrors)
        {
            if (mirror is null || string.IsNullOrWhiteSpace(mirror.SenderDeviceId) ||
                !receivers.ContainsKey(mirror.PeerId) || !senders.ContainsKey(mirror.SenderDeviceId))
                throw new ArgumentException("Each configured PC mirror needs sender and receiver credential references.");
        }

        var fingerprints = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (instance, reference) in clients)
        {
            token.ThrowIfCancellationRequested();
            var secret = await vault.ReadAsync(reference, token).ConfigureAwait(false);
            if (!IsToken(secret))
                throw new InvalidOperationException("A configured desktop credential is missing or malformed.");
            if (!fingerprints.TryAdd(Fingerprint(secret!), instance))
                throw new InvalidOperationException("Configured desktops must have distinct credentials.");
        }
        return new(host.LibraryId, vault, fingerprints, receivers, senders);
    }

    /// <inheritdoc />
    public ValueTask<AuthenticatedHostInstance?> VerifyAsync(string bearerToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(IsToken(bearerToken) && clientFingerprints.TryGetValue(Fingerprint(bearerToken), out var instance)
            ? new AuthenticatedHostInstance(libraryId, instance) : null);
    }

    /// <inheritdoc />
    public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) =>
        ReadRoleAsync(libraryId, receivers.GetValueOrDefault(deviceId), token);

    /// <inheritdoc />
    public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) =>
        ReadRoleAsync(libraryId, senders.GetValueOrDefault(senderDeviceId), token);

    private async ValueTask<string?> ReadRoleAsync(Guid requestedLibrary, string? reference, CancellationToken token)
    {
        if (requestedLibrary != libraryId || reference is null)
            return null;
        var credential = await vault.ReadAsync(reference, token).ConfigureAwait(false);
        return IsToken(credential) ? credential : null;
    }

    private static void AddReference(Dictionary<Guid, string> entries, Guid id, string reference)
    {
        if (id == Guid.Empty || !IsReference(reference) || !entries.TryAdd(id, reference))
            throw new ArgumentException("Host credential references must use valid and unique instance IDs.");
    }

    internal static bool IsReference(string? reference) => reference is { Length: > 0 and <= 256 } &&
        char.IsAsciiLetterOrDigit(reference[0]) && reference.All(character => char.IsAsciiLetterOrDigit(character) || "-._/:".Contains(character));

    private static bool IsToken(string? token) => token is { Length: > 0 and <= 2048 } &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || "-._~+/=".Contains(character));

    private static string Fingerprint(string token)
    {
        var bytes = Encoding.ASCII.GetBytes(token);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
