using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.HostLibrary;

/// <summary>Previews and confirms maintenance on the registered library host.</summary>
/// <remarks>2026-11-24: Scope exact maintenance approvals for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public interface ILibraryMaintenanceHost
{
    /// <summary>Previews the configured organization of the selected tracks.</summary>
    Task<Outcome<OrganizePlan>> PreviewAsync(GetOrganizePlan query, RequestContext context, CancellationToken token);

    /// <summary>Previews the exact effects of the selected removal mode.</summary>
    Task<Outcome<RemovalPlan>> PreviewAsync(GetRemovalPlan query, RequestContext context, CancellationToken token);

    /// <summary>Admits the reviewed organization plan with the originating operation ID.</summary>
    Task<Outcome<LibraryJobReceipt>> ConfirmAsync(OrganizeTracks command, CommandContext context, CancellationToken token);

    /// <summary>Admits removal in the exact explicitly approved mode.</summary>
    Task<Outcome<LibraryJobReceipt>> ConfirmAsync(RemoveTracks command, CommandContext context, CancellationToken token);
}

/// <summary>Dispatches maintenance previews and directs confirmations to the host or the local batch.</summary>
/// <remarks>2026-11-24: Bind STB18 for FR-BEETS-001#ac-edit and ac-recover and FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-revert.</remarks>
public sealed class LibraryMaintenanceHandlers(LibraryBatchScope scope, ILibraryMaintenanceHost host, LibraryBatchHandlers batches) :
    IQueryHandler<GetOrganizePlan, OrganizePlan>, IQueryHandler<GetRemovalPlan, RemovalPlan>,
    ICommandHandler<OrganizeTracks, LibraryJobReceipt>, ICommandHandler<RemoveTracks, LibraryJobReceipt>
{
    /// <summary>Registers the four maintenance requests on the existing CQRS dispatcher.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetOrganizePlan, OrganizePlan>(this)
        .RegisterQuery<GetRemovalPlan, RemovalPlan>(this)
        .RegisterCommand<OrganizeTracks, LibraryJobReceipt>(this)
        .RegisterCommand<RemoveTracks, LibraryJobReceipt>(this);

    /// <inheritdoc />
    public async Task<Outcome<OrganizePlan>> HandleAsync(GetOrganizePlan query, RequestContext context, CancellationToken token)
    {
        if (!CanPreview(query.LibraryId, query.CatalogRevision, query.TrackIds, context))
            return Invalid<OrganizePlan>("invalid-library-maintenance-selection");
        var result = await host.PreviewAsync(query, context, token).ConfigureAwait(false);
        if (result is Outcome<OrganizePlan>.Success { Value: var plan } &&
            (!ValidOrganize(plan) || plan.CatalogRevision != query.CatalogRevision || !plan.TrackIds.SequenceEqual(query.TrackIds)))
            return InvalidPreview<OrganizePlan>();
        return result;
    }

    /// <inheritdoc />
    public async Task<Outcome<RemovalPlan>> HandleAsync(GetRemovalPlan query, RequestContext context, CancellationToken token)
    {
        if (!CanPreview(query.LibraryId, query.CatalogRevision, query.TrackIds, context) || !Enum.IsDefined(query.Mode))
            return Invalid<RemovalPlan>("invalid-library-maintenance-selection");
        var result = await host.PreviewAsync(query, context, token).ConfigureAwait(false);
        if (result is Outcome<RemovalPlan>.Success { Value: var plan } && (!ValidRemoval(plan) || plan.Mode != query.Mode ||
            plan.CatalogRevision != query.CatalogRevision || !plan.TrackIds.SequenceEqual(query.TrackIds)))
            return InvalidPreview<RemovalPlan>();
        return result;
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(OrganizeTracks command, CommandContext context, CancellationToken token)
    {
        if (!CanConfirm(command.PlanId, command.ExpectedCatalogRevision, context))
            return Task.FromResult(Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval"));
        var reviewed = command.ReviewedPlan;
        if (reviewed is not null && (!Matches(reviewed.PlanId, reviewed.CatalogRevision, command.PlanId, command.ExpectedCatalogRevision) ||
            !ValidOrganize(reviewed) || reviewed.Moves.IsEmpty))
            return Task.FromResult(Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval"));
        return StageOrConfirm<LibraryBatchChange.Organize>(context, reviewed is null ? null : new(reviewed),
            prior => Matches(prior.Plan.PlanId, prior.Plan.CatalogRevision, command.PlanId, command.ExpectedCatalogRevision) &&
                (reviewed is null || (reviewed.TrackIds.SequenceEqual(prior.Plan.TrackIds) && reviewed.Moves.SequenceEqual(prior.Plan.Moves))),
            ct => host.ConfirmAsync(command, context, ct), token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(RemoveTracks command, CommandContext context, CancellationToken token)
    {
        if (!CanConfirm(command.PlanId, command.ExpectedCatalogRevision, context) || command.Approval is not { } approval ||
            approval.PlanId != command.PlanId || !Enum.IsDefined(approval.Mode))
            return Task.FromResult(Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval"));
        var reviewed = command.ReviewedPlan;
        if (reviewed is not null && (!Matches(reviewed.PlanId, reviewed.CatalogRevision, command.PlanId, command.ExpectedCatalogRevision) ||
            !ValidRemoval(reviewed) || reviewed.Mode != approval.Mode))
            return Task.FromResult(Invalid<LibraryJobReceipt>("invalid-library-maintenance-approval"));
        return StageOrConfirm<LibraryBatchChange.Remove>(context, reviewed is null ? null : new(reviewed, approval),
            prior => prior.Approval == approval && Matches(prior.Plan.PlanId, prior.Plan.CatalogRevision, command.PlanId, command.ExpectedCatalogRevision) &&
                (reviewed is null || (reviewed.TrackIds.SequenceEqual(prior.Plan.TrackIds) && reviewed.AudioPaths.SequenceEqual(prior.Plan.AudioPaths))),
            ct => host.ConfirmAsync(command, context, ct), token);
    }

    private Task<Outcome<LibraryJobReceipt>> StageOrConfirm<TChange>(CommandContext context, TChange? change, Predicate<TChange> matchesReplay,
        Func<CancellationToken, Task<Outcome<LibraryJobReceipt>>> confirm, CancellationToken token) where TChange : LibraryBatchChange =>
        batches.RouteMutationAsync<TChange, LibraryJobReceipt>(context, confirm,
            _ => Task.FromResult(change is null ? Invalid<TChange>("library-maintenance-preview-required") : new Outcome<TChange>.Success(change)),
            matchesReplay, (_, receipt) => new(Guid.Empty, scope.LibraryId, receipt.Version.Revision, receipt), token);

    private bool CanPreview(Guid library, long revision, ImmutableArray<Guid> ids, RequestContext context) =>
        library != Guid.Empty && library == scope.LibraryId && revision > 0 && ValidSelection(ids) && Authorized(context);

    private bool CanConfirm(Guid planId, long revision, CommandContext context) =>
        planId != Guid.Empty && revision > 0 && context.OperationId != Guid.Empty && Authorized(context.Request);

    private bool Authorized(RequestContext context) => scope.OriginInstanceId != Guid.Empty && context.OriginInstanceId == scope.OriginInstanceId;

    private bool ValidOrganize(OrganizePlan plan)
    {
        if (plan.PlanId == Guid.Empty || plan.LibraryId != scope.LibraryId || !ValidSelection(plan.TrackIds) || plan.Moves.IsDefault ||
            plan.Moves.Any(move => move is null || !IsRelative(move.Source) || !IsRelative(move.Destination) || !plan.TrackIds.Contains(move.TrackId)))
            return false;
        var sources = plan.Moves.Select(move => move.Source).ToHashSet(StringComparer.Ordinal);
        return plan.Moves.Select(move => move.TrackId).Distinct().Count() == plan.Moves.Length &&
            plan.Moves.Select(move => move.Destination).Distinct(StringComparer.Ordinal).Count() == plan.Moves.Length &&
            plan.Moves.All(move => !sources.Contains(move.Destination));
    }

    private bool ValidRemoval(RemovalPlan plan) => plan.PlanId != Guid.Empty && plan.LibraryId == scope.LibraryId && ValidSelection(plan.TrackIds) &&
        !plan.AudioPaths.IsDefault && (plan.Mode == RemovalMode.CatalogOnly ? plan.AudioPaths.IsEmpty :
            plan.Mode == RemovalMode.CatalogAndAudio && plan.AudioPaths.Length == plan.TrackIds.Length && plan.AudioPaths.All(IsRelative));

    private static bool ValidSelection(ImmutableArray<Guid> ids) => !ids.IsDefaultOrEmpty && !ids.Contains(Guid.Empty) && ids.Distinct().Count() == ids.Length;

    private static bool IsRelative(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) && path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static bool Matches(Guid plan, long revision, Guid expectedPlan, long expectedRevision) => plan == expectedPlan && revision == expectedRevision;

    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);
    private static Outcome<T> InvalidPreview<T>() => new Outcome<T>.DependencyFailure("library-maintenance-preview-mismatch", true);
}
