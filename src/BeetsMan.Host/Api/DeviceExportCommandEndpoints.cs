using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeetsMan.Host.Api;

/// <summary>Exposes authorized device export review and publication commands.</summary>
/// <remarks>2026-11-24: Carry the exact reviewed revision and confirmation for FR-FILESYNC-001#ac-safety.</remarks>
public static class DeviceExportCommandEndpoints
{
    /// <summary>Registers both version 1 device export commands.</summary>
    public static void MapDeviceExportCommands(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(DeviceExportCommandRoutes.PreparePattern, PrepareAsync);
        routes.MapPost(DeviceExportCommandRoutes.PublishPattern, PublishAsync);
    }

    private static Task<IResult> PrepareAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        StagedDeviceExportWorkflow workflow, CancellationToken token) => HostLibraryMutation.RunAsync<PrepareDeviceExportRequest, PrepareDeviceExportResponse>(
        libraryId, context, verifier, (scope, caller, command, ct) => PrepareResultAsync(workflow, scope, caller, command, ct), "export-preview", token);

    private static Task<IResult> PublishAsync(Guid libraryId, HttpContext context, IHostAccessTokenVerifier verifier,
        StagedDeviceExportWorkflow workflow, CancellationToken token) => HostLibraryMutation.RunAsync<PublishDeviceExportRequest, DeviceExportCommandResult>(
        libraryId, context, verifier, (scope, caller, command, ct) => PublishResultAsync(workflow, scope, caller, command, ct), "export-publish", token);

    private static async Task<HostOutcome<PrepareDeviceExportResponse>> PrepareResultAsync(StagedDeviceExportWorkflow workflow,
        Guid libraryId, AuthenticatedHostInstance caller, PrepareDeviceExportRequest command, CancellationToken token)
    {
        if (command.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<PrepareDeviceExportResponse>.Forbidden();
        if (command.PlanId is null && command.ExpectedPlanRevision is null)
            return new HostOutcome<PrepareDeviceExportResponse>.Rejected("export-plan-required");
        if (command.PlanId is not { } planId || command.ExpectedPlanRevision is not { } revision)
            return new HostOutcome<PrepareDeviceExportResponse>.Rejected("invalid-export-preview-intent");
        var result = await workflow.PrepareAsync(libraryId, caller,
            new(command.OperationId, planId, revision, command.DeviceId, command.ProfileId, command.ProfileRevision, command.RelativePathsByTrack), token)
            .ConfigureAwait(false);
        if (result is not HostOutcome<PreparedDeviceExportPublication>.Success { Value: var preview })
            return result.PropagateFailure<PrepareDeviceExportResponse>();
        return new HostOutcome<PrepareDeviceExportResponse>.Success(new(command.OperationId, preview.Plan,
            [.. preview.Operations.Select(MapChange)], [.. preview.Issues.Select(issue => new ExportPreviewIssue(issue.Code, issue.Path))],
            preview.RemovalProposal?.Paths ?? []) { ReassignsProfile = preview.PreviousProfile is not null });
    }

    private static async Task<HostOutcome<DeviceExportCommandResult>> PublishResultAsync(StagedDeviceExportWorkflow workflow,
        Guid libraryId, AuthenticatedHostInstance caller, PublishDeviceExportRequest command, CancellationToken token)
    {
        if (command.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<DeviceExportCommandResult>.Forbidden();
        DeviceExportRemovalApproval? approval = command.Approval is { } supplied ? new(supplied.ProposalId, supplied.PlanId, supplied.PlanRevision) : null;
        var published = await workflow.PublishAsync(libraryId, caller, new(command.OperationId, command.PlanId, command.PlanRevision, approval), token)
            .ConfigureAwait(false);
        return published switch
        {
            HostOutcome<DeviceExportPublicationReservation>.Success { Value: DeviceExportPublicationReservation.Completed { Receipt: var receipt } } =>
                new HostOutcome<DeviceExportCommandResult>.Success(new DeviceExportCommandResult.Published(command.OperationId, receipt.PlanId,
                    receipt.PlanRevision, receipt.PublicationId, receipt.TransferId)),
            HostOutcome<DeviceExportPublicationReservation>.Success { Value: DeviceExportPublicationReservation.ConfirmationRequired { Proposal: var proposal } } =>
                new HostOutcome<DeviceExportCommandResult>.Success(new DeviceExportCommandResult.ConfirmationRequired(command.OperationId, proposal.PlanId,
                    proposal.PlanRevision, proposal.ProposalId, proposal.Paths)),
            HostOutcome<DeviceExportPublicationReservation>.Success => throw new InvalidOperationException("Publication returned an unresolved internal step."),
            _ => published.PropagateFailure<DeviceExportCommandResult>()
        };
    }

    private static ExportFileChange MapChange(DeviceExportFileOperation operation)
    {
        var kind = operation.Kind switch
        {
            DeviceExportOperationKind.Add => ExportFileChangeKind.Add,
            DeviceExportOperationKind.Replace => ExportFileChangeKind.Replace,
            DeviceExportOperationKind.Move => ExportFileChangeKind.Move,
            DeviceExportOperationKind.Remove => ExportFileChangeKind.Remove,
            _ => throw new InvalidOperationException("Unknown export file operation.")
        };
        return new(kind, operation.Before?.Path, operation.After?.Path);
    }
}
