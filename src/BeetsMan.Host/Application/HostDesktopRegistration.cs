using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Records activity for a credential already verified in the requested library scope.</summary>
/// <remarks>2026-11-25: Keep the SCR42 activity projection independent of authorization revisions for FR-RECORDS-001#ac-register.</remarks>
internal interface IHostDesktopActivity
{
    Task ObserveAsync(Guid library, Guid instance, CancellationToken token);
}

/// <summary>Provides scoped desktop registry reads and transactional enrollment and revocation.</summary>
/// <remarks>2026-11-25: Keep exact operations and reviewed revisions for FR-RECORDS-001#ac-register.</remarks>
internal interface IHostDesktopRegistration
{
    Task<HostOutcome<DesktopRegistrationDirectory>> ReadAsync(Guid library, AuthenticatedHostInstance caller, CancellationToken token);
    Task<HostOutcome<RegisterDesktopResponse>> RegisterAsync(Guid library, AuthenticatedHostInstance caller, RegisterDesktopRequest request, CancellationToken token);
    Task<HostOutcome<RevokeDesktopResponse>> RevokeAsync(Guid library, AuthenticatedHostInstance caller, RevokeDesktopRequest request, CancellationToken token);
}

/// <summary>Validates registration commands at the authenticated host boundary.</summary>
/// <remarks>2026-11-25: Require exact identity and a provisioned Operator for FR-RECORDS-001#ac-register.</remarks>
internal sealed class HostDesktopRegistration(IHostDesktopRegistration store)
{
    /// <summary>Reads the roster for an authenticated and scoped credential.</summary>
    public Task<HostOutcome<DesktopRegistrationDirectory>> ReadAsync(Guid library, AuthenticatedHostInstance caller, CancellationToken token) =>
        Matches(library, caller, caller.InstanceId) ? store.ReadAsync(library, caller, token) : Forbidden<DesktopRegistrationDirectory>();

    /// <summary>Accepts only a request to register the identity bound to the submitted credential.</summary>
    public Task<HostOutcome<RegisterDesktopResponse>> RegisterAsync(Guid library, AuthenticatedHostInstance caller, RegisterDesktopRequest request,
        CancellationToken token)
    {
        if (!Matches(library, caller, request.OriginInstanceId))
            return Forbidden<RegisterDesktopResponse>();
        if (!DesktopRegistrationRoutes.IsValidName(request.Name) || request.OperationId == Guid.Empty || request.ExpectedRevision < 0)
            return Task.FromResult<HostOutcome<RegisterDesktopResponse>>(new HostOutcome<RegisterDesktopResponse>.Rejected("invalid-desktop-registration"));
        return store.RegisterAsync(library, caller, request with { Name = request.Name.Trim() }, token);
    }

    /// <summary>Admits another desktop's revocation only from a configured Operator.</summary>
    public Task<HostOutcome<RevokeDesktopResponse>> RevokeAsync(Guid library, AuthenticatedHostInstance caller, RevokeDesktopRequest request,
        CancellationToken token)
    {
        if (!Matches(library, caller, request.OriginInstanceId) || !caller.CanManageRegistrations || request.InstanceId == caller.InstanceId)
            return Forbidden<RevokeDesktopResponse>();
        if (request.InstanceId == Guid.Empty || request.OperationId == Guid.Empty || request.ExpectedRevision < 0)
            return Task.FromResult<HostOutcome<RevokeDesktopResponse>>(new HostOutcome<RevokeDesktopResponse>.Rejected("invalid-desktop-revocation"));
        return store.RevokeAsync(library, caller, request, token);
    }

    private static bool Matches(Guid library, AuthenticatedHostInstance caller, Guid origin) =>
        library != Guid.Empty && origin != Guid.Empty && caller.LibraryId == library && caller.InstanceId == origin;

    private static Task<HostOutcome<T>> Forbidden<T>() => Task.FromResult<HostOutcome<T>>(new HostOutcome<T>.Forbidden());
}
