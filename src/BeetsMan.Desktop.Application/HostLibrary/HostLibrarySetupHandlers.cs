using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.HostLibrary;

/// <summary>Projects the configured host library and submits explicit initialization on the desktop.</summary>
/// <remarks>2026-11-25: Route the three STB08 setup interactions for FR-BEETS-001#ac-open.</remarks>
public interface IHostLibrarySetup
{
    Task<Outcome<HostLibraryState>> ReadAsync(CancellationToken token);
    Task<Outcome<HostLibraryConfiguration>> ReadConfigurationAsync(CancellationToken token);
    Task<Outcome<HostLibraryState>> InitializeAsync(HostLibrarySettings settings, long expectedRevision, Guid operation, CancellationToken token);
    Task<Outcome<HostLibraryConfiguration>> SaveConfigurationAsync(HostLibrarySettings settings, long expectedRevision, Guid operation, CancellationToken token);
}

/// <summary>Dispatches setup against the current library and registered desktop identity.</summary>
/// <remarks>2026-11-25: Keep host-only library setup inert while disconnected for FR-BEETS-001#ac-open.</remarks>
public sealed class HostLibrarySetupHandlers(Guid library, Guid origin, IHostLibrarySetup? host) :
    IQueryHandler<GetHostLibraryState, HostLibraryState>, IQueryHandler<GetHostLibraryConfiguration, HostLibraryConfiguration>,
    ICommandHandler<InitializeHostLibrary, HostLibraryState>, ICommandHandler<SaveHostLibraryConfiguration, HostLibraryConfiguration>
{
    /// <summary>Registers the existing host library setup contracts in the workspace.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetHostLibraryState, HostLibraryState>(this)
        .RegisterQuery<GetHostLibraryConfiguration, HostLibraryConfiguration>(this)
        .RegisterCommand<InitializeHostLibrary, HostLibraryState>(this)
        .RegisterCommand<SaveHostLibraryConfiguration, HostLibraryConfiguration>(this);

    /// <inheritdoc />
    public Task<Outcome<HostLibraryState>> HandleAsync(GetHostLibraryState request, RequestContext context, CancellationToken token) =>
        Matches(request.LibraryId, context) ? host?.ReadAsync(token) ?? Offline<HostLibraryState>() : Invalid<HostLibraryState>();

    /// <inheritdoc />
    public Task<Outcome<HostLibraryConfiguration>> HandleAsync(GetHostLibraryConfiguration request, RequestContext context, CancellationToken token) =>
        Matches(request.LibraryId, context) ? host?.ReadConfigurationAsync(token) ?? Offline<HostLibraryConfiguration>() : Invalid<HostLibraryConfiguration>();

    /// <inheritdoc />
    public Task<Outcome<HostLibraryState>> HandleAsync(InitializeHostLibrary request, CommandContext context, CancellationToken token) =>
        Matches(request.LibraryId, context.Request) && request.ExpectedRevision >= 0 && context.OperationId != Guid.Empty &&
        request.ApprovedSettings is { ConfigurationPath.Length: > 0, CatalogPath.Length: > 0, SourceMusicRoot.Length: > 0 }
            ? host?.InitializeAsync(request.ApprovedSettings, request.ExpectedRevision, context.OperationId, token) ?? Offline<HostLibraryState>()
            : Invalid<HostLibraryState>();

    /// <inheritdoc />
    public Task<Outcome<HostLibraryConfiguration>> HandleAsync(SaveHostLibraryConfiguration request, CommandContext context, CancellationToken token) =>
        Matches(request.LibraryId, context.Request) && request.ExpectedRevision >= 0 && context.OperationId != Guid.Empty &&
        request.Edits is { ConfigurationPath.Length: > 0, CatalogPath.Length: > 0, SourceMusicRoot.Length: > 0 }
            ? host?.SaveConfigurationAsync(request.Edits, request.ExpectedRevision, context.OperationId, token) ?? Offline<HostLibraryConfiguration>()
            : Invalid<HostLibraryConfiguration>();

    private bool Matches(Guid scope, RequestContext context) => scope == library && context.OriginInstanceId == origin;
    private static Task<Outcome<T>> Invalid<T>() => Task.FromResult<Outcome<T>>(new Outcome<T>.Invalid([new("invalid-host-library-setup-request")]));
    private static Task<Outcome<T>> Offline<T>() => Task.FromResult<Outcome<T>>(new Outcome<T>.DependencyFailure("beets-library-host-unavailable", true));
}
