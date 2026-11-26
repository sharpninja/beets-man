using BeetsMan.LocalCredentials;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Resolves one provisioned desktop credential locally for registration and subsequent host access.</summary>
/// <remarks>2026-11-25: Provide an opaque selection token and keep the credential in the OS vault for FR-RECORDS-001#ac-register.</remarks>
public sealed class OperatingSystemDesktopHostCredentials : IDesktopRegistrationCredentials
{
    private readonly Guid library;
    private readonly Guid instance;
    private readonly string reference;

    /// <summary>Binds a local OS credential reference to one desktop and library.</summary>
    public OperatingSystemDesktopHostCredentials(Guid library, Guid instance, string reference)
    {
        if (library == Guid.Empty || instance == Guid.Empty || !OperatingSystemCredentialLookup.IsReference(reference))
            throw new ArgumentException("A library, desktop, and valid local credential reference are required.");
        this.library = library;
        this.instance = instance;
        this.reference = reference;
    }

    /// <summary>Gets the opaque handle for the current local selection.</summary>
    public Guid SelectionToken { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public async ValueTask<string?> GetTokenAsync(Guid libraryId, Guid instanceId, CancellationToken cancellationToken)
    {
        if (libraryId != library || instanceId != instance)
            return null;
        var value = await OperatingSystemCredentialLookup.ReadAsync(reference, cancellationToken).ConfigureAwait(false);
        return OperatingSystemCredentialLookup.IsBearerToken(value) ? value : null;
    }

    /// <inheritdoc />
    public ValueTask<string?> GetSelectedTokenAsync(Guid libraryId, Guid instanceId, Guid selection, CancellationToken token) =>
        selection == SelectionToken ? GetTokenAsync(libraryId, instanceId, token) : ValueTask.FromResult<string?>(null);
}
