using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Resolves the opaque selection of a credential for the configured desktop and library.</summary>
/// <remarks>2026-11-25: Keep credentials out of CQRS and host registration payloads for FR-RECORDS-001#ac-register.</remarks>
public interface IDesktopRegistrationCredentials : IRegisteredHostTokenProvider
{
    ValueTask<string?> GetSelectedTokenAsync(Guid library, Guid instance, Guid selection, CancellationToken token);
}

/// <summary>Sends exact desktop registry operations through the configured private HTTPS endpoint.</summary>
/// <remarks>2026-11-25: Verify response operation, scope, and target for FR-RECORDS-001#ac-register.</remarks>
/// <remarks>2026-11-25: Preserve the host's optional UTC activity timestamp for FR-RECORDS-001#ac-register.</remarks>
public sealed class HttpDesktopRegistrationHost(HttpClient client, Uri host, Guid library, Guid origin, IRegisteredHostTokenProvider credentials)
    : IDesktopRegistrationHost
{
    private readonly Uri directory = RegisteredHostEndpoint.Resolve(host, library, DesktopRegistrationRoutes.DirectoryPath);
    private readonly Uri revoke = RegisteredHostEndpoint.Resolve(host, library, DesktopRegistrationRoutes.RevokePath);

    /// <inheritdoc />
    public Task<Outcome<RegisteredInstancesSnapshot>> ReadAsync(CancellationToken token) =>
        RegisteredHostEndpoint.SendAsync<DesktopRegistrationDirectory, RegisteredInstancesSnapshot>(client, credentials, library,
            new(HttpMethod.Get, directory), origin, "desktop-registry", "desktop-registry-response-mismatch", ProjectDirectory, token);

    /// <inheritdoc />
    public Task<Outcome<RegistrationReceipt>> RegisterAsync(RegistrationDraft draft, long expectedRevision, Guid operation, CancellationToken token)
    {
        if (!MatchesHost(draft.Host) || credentials is not IDesktopRegistrationCredentials selected)
            return Task.FromResult<Outcome<RegistrationReceipt>>(new Outcome<RegistrationReceipt>.Invalid([new("desktop-registration-host-or-selection-invalid")]));
        var name = draft.Name.Trim();
        var body = new RegisterDesktopRequest(origin, operation, name, expectedRevision);
        return RegisteredHostEndpoint.SendAsync<RegisterDesktopResponse, RegistrationReceipt>(client, new SelectedToken(selected, draft.CredentialSelectionToken),
            library, new(HttpMethod.Post, directory) { Content = JsonContent.Create(body) }, origin, "desktop-registration",
            "desktop-registration-response-mismatch", reply => reply.LibraryId == library && reply.OperationId == operation &&
                reply.Revision >= expectedRevision && reply.Instance is { Revoked: false } row && IsValid(row) &&
                row.InstanceId == origin && row.Name == name ? new(library, Project(row), reply.Revision) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<RegisteredInstancesSnapshot>> RevokeAsync(Guid target, long expectedRevision, Guid operation, CancellationToken token) =>
        RegisteredHostEndpoint.SendAsync<RevokeDesktopResponse, RegisteredInstancesSnapshot>(client, credentials, library,
            new(HttpMethod.Post, revoke) { Content = JsonContent.Create(new RevokeDesktopRequest(origin, operation, target, expectedRevision)) },
            origin, "desktop-revocation", "desktop-revocation-response-mismatch", reply => reply.OperationId == operation &&
                reply.InstanceId == target && reply.Directory is { CanManage: true } roster && roster.Revision >= expectedRevision &&
                ProjectDirectory(roster) is { } projected && projected.Instances.Any(row => row.InstanceId == target && row.Revoked)
                    ? projected : null, token);

    private bool MatchesHost(Uri candidate) => candidate is { IsAbsoluteUri: true } &&
        candidate.Scheme == Uri.UriSchemeHttps && candidate.UserInfo.Length == 0 && candidate.Query.Length == 0 && candidate.Fragment.Length == 0 &&
        RegisteredHostEndpoint.Resolve(candidate, library, DesktopRegistrationRoutes.DirectoryPath) == directory;

    private RegisteredInstancesSnapshot? ProjectDirectory(DesktopRegistrationDirectory reply) =>
        reply.LibraryId == library && reply.Revision >= 0 && !reply.Instances.IsDefault && reply.Instances.All(IsValid) &&
        reply.Instances.Select(row => row.InstanceId).Distinct().Count() == reply.Instances.Length &&
        (!reply.CanManage || reply.Instances.Any(row => row.InstanceId == origin && !row.Revoked))
            ? new(library, reply.Revision, [.. reply.Instances.Select(Project)]) { CanManage = reply.CanManage } : null;

    private static bool IsValid(DesktopRegistrationRow? row) => row is not null && row.InstanceId != Guid.Empty &&
        DesktopRegistrationRoutes.IsValidName(row.Name) && Enum.IsDefined(row.Role) &&
        (row.LastSeenUtc is not { } seen || seen.Offset == TimeSpan.Zero);

    private static RegisteredInstance Project(DesktopRegistrationRow row) => new(row.InstanceId, row.Name, row.Revoked)
        { Role = row.Role, LastSeenUtc = row.LastSeenUtc };

    private sealed class SelectedToken(IDesktopRegistrationCredentials store, Guid selection) : IRegisteredHostTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid instanceId, CancellationToken cancellationToken) =>
            store.GetSelectedTokenAsync(libraryId, instanceId, selection, cancellationToken);
    }
}
