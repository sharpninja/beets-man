using System.Collections.Immutable;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Specifies the local HTTPS listener and certificate-store reference.</summary>
/// <remarks>2026-11-24: Launch a private credential-backed endpoint for FR-RECORDS-001#ac-register.</remarks>
public sealed record LibraryHostListener(string Address, int Port, string CertificateThumbprint);

/// <summary>Associates a registered desktop with its bearer credential reference.</summary>
public sealed record HostClientCredential(Guid InstanceId, string Reference);

/// <summary>Associates a registered receiver with its Syncthing API credential reference.</summary>
public sealed record HostReceiverCredential(Guid DeviceId, string Reference);

/// <summary>Associates a configured sender with its Syncthing API credential reference.</summary>
public sealed record HostSenderCredential(string SyncthingDeviceId, string Reference);

/// <summary>Identifies separately scoped local credentials by reference.</summary>
public sealed record HostCredentialReferences(ImmutableArray<HostClientCredential> Clients,
    ImmutableArray<HostReceiverCredential> Receivers, ImmutableArray<HostSenderCredential> Senders);

/// <summary>Contains the standalone host's settings without the underlying credential values.</summary>
public sealed record LibraryHostLaunchSettings(LibraryHostOptions Host, LibraryHostListener Listener, HostCredentialReferences Credentials);

/// <summary>Reads a named Beets Man entry from a local operating-system credential store.</summary>
public interface ILocalHostCredentialVault
{
    /// <summary>Returns the value of the specified application entry, or null when unavailable.</summary>
    ValueTask<string?> ReadAsync(string reference, CancellationToken token);
}
