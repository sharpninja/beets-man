using BeetsMan.LocalCredentials;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Reads Beets Man credentials from the launching user's platform vault.</summary>
/// <remarks>2026-11-24: Resolve local credential references for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
/// <remarks>2026-11-25: Reuse the same native lookup as the registration desktop for FR-RECORDS-001#ac-register.</remarks>
public sealed class OperatingSystemHostCredentialVault : ILocalHostCredentialVault
{
    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(string reference, CancellationToken token) =>
        OperatingSystemCredentialLookup.ReadAsync(reference, token);
}
