using System.Collections.Immutable;
using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Previews or confirms the approved maintenance effect at the registered HTTPS endpoint.</summary>
/// <remarks>2026-11-24: Preserve library scope, approval mode and operation identity for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class HttpLibraryMaintenanceHost(HttpClient client, Uri host, Guid libraryId, IRegisteredHostTokenProvider credentials) : ILibraryMaintenanceHost
{
    private readonly Uri previewEndpoint = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.LibraryMaintenanceRoutes.PreviewPath);

    /// <inheritdoc />
    public Task<Outcome<OrganizePlan>> PreviewAsync(GetOrganizePlan query, RequestContext context, CancellationToken token) =>
        PreviewAsync(query.LibraryId, new(Wire.LibraryMaintenanceKind.Organize, query.CatalogRevision, query.TrackIds), context,
            plan => plan.AudioPaths.IsEmpty && plan.Moves.All(move => move is not null) ? new OrganizePlan(plan.PlanId, plan.LibraryId, plan.CatalogRevision,
                [.. plan.Moves.Select(move => new FileMove(move.Source, move.Destination, move.TrackId))], plan.TrackIds) : null, token);

    /// <inheritdoc />
    public Task<Outcome<RemovalPlan>> PreviewAsync(GetRemovalPlan query, RequestContext context, CancellationToken token) =>
        PreviewAsync(query.LibraryId, new(RemovalKind(query.Mode), query.CatalogRevision, query.TrackIds), context,
            plan => plan.Moves.IsEmpty ? new RemovalPlan(plan.PlanId, plan.LibraryId, plan.CatalogRevision, query.Mode, plan.TrackIds, plan.AudioPaths) : null,
            token);

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> ConfirmAsync(OrganizeTracks command, CommandContext context, CancellationToken token) =>
        ConfirmAsync(command.PlanId, command.ExpectedCatalogRevision, Wire.LibraryMaintenanceKind.Organize, context, token);

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> ConfirmAsync(RemoveTracks command, CommandContext context, CancellationToken token) =>
        command.Approval is { } approval && approval.PlanId == command.PlanId
            ? ConfirmAsync(command.PlanId, command.ExpectedCatalogRevision, RemovalKind(approval.Mode), context, token)
            : Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval");

    private Task<Outcome<TResult>> PreviewAsync<TResult>(Guid requestedLibrary, Wire.LibraryMaintenancePreviewRequest preview,
        RequestContext context, Func<Wire.LibraryMaintenancePlan, TResult?> project, CancellationToken token) where TResult : class
    {
        if (requestedLibrary != libraryId || context.OriginInstanceId is not { } origin || origin == Guid.Empty ||
            preview.Kind == Wire.LibraryMaintenanceKind.Unspecified || preview.TrackIds.IsDefaultOrEmpty)
            return Invalid<TResult>("invalid-library-maintenance-selection");
        var request = new HttpRequestMessage(HttpMethod.Post, previewEndpoint) { Content = JsonContent.Create(preview) };
        return RegisteredHostEndpoint.SendAsync<Wire.LibraryMaintenancePlan, TResult>(client, credentials, libraryId, request, origin,
            "library-maintenance-preview", "library-maintenance-preview-mismatch", plan =>
                plan.LibraryId == libraryId && plan.PlanId != Guid.Empty && plan.Kind == preview.Kind && plan.CatalogRevision == preview.CatalogRevision &&
                !plan.TrackIds.IsDefault && plan.TrackIds.SequenceEqual(preview.TrackIds) && !plan.Moves.IsDefault && !plan.AudioPaths.IsDefault
                    ? project(plan) : null, token);
    }

    private Task<Outcome<LibraryJobReceipt>> ConfirmAsync(Guid plan, long revision, Wire.LibraryMaintenanceKind kind, CommandContext context,
        CancellationToken token)
    {
        if (plan == Guid.Empty || revision <= 0 || kind == Wire.LibraryMaintenanceKind.Unspecified || context.OperationId == Guid.Empty ||
            context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval");
        var endpoint = RegisteredHostEndpoint.Resolve(host, libraryId, library => Wire.LibraryMaintenanceRoutes.ConfirmPath(library, plan));
        var confirm = new Wire.ConfirmLibraryMaintenanceRequest(origin, context.OperationId, kind, revision, kind == Wire.LibraryMaintenanceKind.RemoveAudio);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(confirm) };
        return RegisteredHostEndpoint.SendAsync<Wire.LibraryMaintenanceReceipt, LibraryJobReceipt>(client, credentials, libraryId, request, origin,
            "library-maintenance-confirm", "library-maintenance-receipt-mismatch", receipt =>
                receipt.LibraryId == libraryId && receipt.OperationId == context.OperationId && receipt.JobId != Guid.Empty && receipt.Revision > 0
                    ? new(receipt.JobId, receipt.LibraryId, receipt.Revision) : null, token);
    }

    private static Wire.LibraryMaintenanceKind RemovalKind(RemovalMode mode) => mode switch
    {
        RemovalMode.CatalogOnly => Wire.LibraryMaintenanceKind.RemoveCatalog,
        RemovalMode.CatalogAndAudio => Wire.LibraryMaintenanceKind.RemoveAudio,
        _ => Wire.LibraryMaintenanceKind.Unspecified
    };

    private static Task<Outcome<T>> Invalid<T>(string code) => Task.FromResult<Outcome<T>>(new Outcome<T>.Invalid([new(code)]));
}
