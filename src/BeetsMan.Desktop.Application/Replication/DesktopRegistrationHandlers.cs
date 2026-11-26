using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Application.Replication;

/// <summary>Connects registration commands to the configured versioned host registry.</summary>
/// <remarks>2026-11-25: Keep credential handling in the desktop adapter for FR-RECORDS-001#ac-register.</remarks>
public interface IDesktopRegistrationHost
{
    Task<Outcome<RegisteredInstancesSnapshot>> ReadAsync(CancellationToken token);
    Task<Outcome<RegistrationReceipt>> RegisterAsync(RegistrationDraft draft, long expectedRevision, Guid operation, CancellationToken token);
    Task<Outcome<RegisteredInstancesSnapshot>> RevokeAsync(Guid target, long expectedRevision, Guid operation, CancellationToken token);
}

/// <summary>Routes the roster, self-enrollment, and revocation under the local desktop identity.</summary>
/// <remarks>2026-11-25: Bind the existing STB09 commands to the registered library for FR-RECORDS-001#ac-register.</remarks>
public sealed class DesktopRegistrationHandlers(Guid library, Guid origin, IDesktopRegistrationHost? host) :
    IQueryHandler<GetRegisteredInstances, RegisteredInstancesSnapshot>, ICommandHandler<RegisterInstance, RegistrationReceipt>,
    ICommandHandler<RevokeInstance, RegisteredInstancesSnapshot>
{
    /// <summary>Registers the desktop registry operations in the normal workspace.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetRegisteredInstances, RegisteredInstancesSnapshot>(this)
        .RegisterCommand<RegisterInstance, RegistrationReceipt>(this)
        .RegisterCommand<RevokeInstance, RegisteredInstancesSnapshot>(this);

    /// <inheritdoc />
    public Task<Outcome<RegisteredInstancesSnapshot>> HandleAsync(GetRegisteredInstances request, RequestContext context, CancellationToken token) =>
        Matches(request.LibraryId, context) ? host?.ReadAsync(token) ?? Unconfigured<RegisteredInstancesSnapshot>() : Invalid<RegisteredInstancesSnapshot>();

    /// <inheritdoc />
    public Task<Outcome<RegistrationReceipt>> HandleAsync(RegisterInstance request, CommandContext context, CancellationToken token) =>
        Matches(request.LibraryId, context.Request) && context.OperationId != Guid.Empty && request.ExpectedRevision >= 0 &&
        request.Draft is { CredentialSelectionToken: var selection, Host: { IsAbsoluteUri: true } endpoint } &&
        selection != Guid.Empty && endpoint.Scheme == Uri.UriSchemeHttps && DesktopRegistrationRoutes.IsValidName(request.Draft.Name)
            ? host?.RegisterAsync(request.Draft, request.ExpectedRevision, context.OperationId, token) ?? Unconfigured<RegistrationReceipt>()
            : Invalid<RegistrationReceipt>();

    /// <inheritdoc />
    public Task<Outcome<RegisteredInstancesSnapshot>> HandleAsync(RevokeInstance request, CommandContext context, CancellationToken token) =>
        Matches(request.LibraryId, context.Request) && context.OperationId != Guid.Empty && request.ExpectedRevision >= 0 &&
        request.InstanceId != Guid.Empty && request.InstanceId != origin
            ? host?.RevokeAsync(request.InstanceId, request.ExpectedRevision, context.OperationId, token) ?? Unconfigured<RegisteredInstancesSnapshot>()
            : Invalid<RegisteredInstancesSnapshot>();

    private bool Matches(Guid scope, RequestContext context) => scope == library && context.OriginInstanceId == origin;
    private static Task<Outcome<T>> Invalid<T>() => Task.FromResult<Outcome<T>>(new Outcome<T>.Invalid([new("invalid-desktop-registry-request")]));
    private static Task<Outcome<T>> Unconfigured<T>() =>
        Task.FromResult<Outcome<T>>(new Outcome<T>.DependencyFailure("desktop-registry-host-unconfigured", true));
}
