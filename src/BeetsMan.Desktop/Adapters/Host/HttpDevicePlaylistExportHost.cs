using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Reads layout and plan data and submits playlist drops through the registered library host.</summary>
/// <remarks>2026-11-24: Bind the CQRS playlist drop, scoped reads and exact review commands for FR-FILESYNC-001#ac-layouts, ac-playlist-drop and ac-safety.</remarks>
public sealed partial class HttpDevicePlaylistExportHost(HttpClient client, Uri host, Guid libraryId, IRegisteredHostTokenProvider credentials)
    : IPlaylistExportHost, IDeviceExportReadHost, IDeviceExportCommandHost, IDeviceExportTransferHost, IDeviceSyncListEditHost, IMediaMirrorHost, IAndroidArrivalHost
{
    private readonly Uri endpoint = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.DevicePlaylistStageRoute.Path);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> StagePlaylistAsync(StagePlaylistForDevice request, CommandContext context, CancellationToken cancellationToken)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportPlan>("export-stage-origin-required"));

        var body = new Wire.StageDevicePlaylistRequest(origin, context.OperationId, request.PlaylistId, request.DeviceId,
            request.ProfileId, request.ProfileRevision, request.PlanId, request.ExpectedPlanRevision);
        return SendAsync<Wire.StageDevicePlaylistResponse, DeviceExportPlan>(new(HttpMethod.Post, endpoint) { Content = JsonContent.Create(body) },
            origin, "export-stage", "export-stage-host-receipt-mismatch", response => response.OperationId == context.OperationId &&
                response.Plan is { } plan && Matches(request, plan) ? MapPlan(plan) : null, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Carry the ordered STB10 track drop and verify the scoped receipt for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    public Task<Outcome<DeviceExportPlan>> StageTracksAsync(StageTracksForDevice request, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportPlan>("export-stage-origin-required"));
        if (request.OrderedTrackIds.IsDefaultOrEmpty)
            return Task.FromResult(Invalid<DeviceExportPlan>("invalid-export-target"));

        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.DeviceSyncListEditRoutes.StageTracksPath);
        var body = new Wire.StageDeviceTracksRequest(origin, context.OperationId, request.OrderedTrackIds, request.DeviceId,
            request.ProfileId, request.ProfileRevision, request.PlanId, request.ExpectedPlanRevision);
        return SendAsync<Wire.DeviceSyncListEditResponse, DeviceExportPlan>(new(HttpMethod.Post, route) { Content = JsonContent.Create(body) },
            origin, "export-stage", "export-stage-host-receipt-mismatch", response => response.OperationId == context.OperationId &&
                response.Plan is { } plan && MatchesEdit(plan, request.PlanId, request.ExpectedPlanRevision ?? 0) &&
                plan.DeviceId == request.DeviceId && plan.ProfileId == request.ProfileId && plan.ProfileRevision == request.ProfileRevision &&
                request.OrderedTrackIds.All(plan.Entries.OfType<Wire.ExportEntry.Audio>().Where(audio => audio.IsDirect)
                    .Select(audio => audio.TrackId).ToHashSet().Contains) ? MapPlan(plan) : null, token);
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Allow staged-intent removal through STB10 with no share file writes for FR-FILESYNC-001#ac-safety.</remarks>
    public Task<Outcome<DeviceExportPlan>> EditPlanAsync(EditDeviceExportPlan request, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportPlan>("export-edit-origin-required"));
        if (request.Edit is not DeviceExportEdit.Remove { EntryIds.IsDefaultOrEmpty: false } removal)
            return Task.FromResult(Invalid<DeviceExportPlan>("invalid-export-removal-intent"));

        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.DeviceSyncListEditRoutes.RemovePath);
        var body = new Wire.RemoveDeviceExportEntriesRequest(origin, context.OperationId, request.PlanId, removal.EntryIds, request.ExpectedPlanRevision);
        var removed = removal.EntryIds.ToHashSet();
        return SendAsync<Wire.DeviceSyncListEditResponse, DeviceExportPlan>(new(HttpMethod.Post, route) { Content = JsonContent.Create(body) },
            origin, "export-edit", "export-edit-host-receipt-mismatch", response => response.OperationId == context.OperationId &&
                response.Plan is { } plan && MatchesEdit(plan, request.PlanId, request.ExpectedPlanRevision) &&
                plan.Entries.Where(entry => removed.Contains(entry.EntryId)).All(entry => entry is Wire.ExportEntry.Audio { IsDirect: false })
                ? MapPlan(plan) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<DeviceLayoutsSnapshot>> ReadLayoutsAsync(GetDeviceLayouts query, RequestContext context, CancellationToken token)
    {
        if (context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<DeviceLayoutsSnapshot>("export-read-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, id => Wire.DeviceExportQueryRoutes.LayoutsPath(id, query.DeviceId));
        return SendAsync<Wire.DeviceLayoutChoices, DeviceLayoutsSnapshot>(new(HttpMethod.Get, route), origin, "export-read",
            "export-read-host-response-mismatch", result => MapLayouts(query.DeviceId, result), token);
    }

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> ReadPlanAsync(GetDeviceExportPlan query, RequestContext context, CancellationToken token)
    {
        if (context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportPlan>("export-read-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, id => Wire.DeviceExportQueryRoutes.PlanPath(id, query.DeviceId));
        var parameters = new List<string>(3);
        if (query.PlanId is { } planId)
            parameters.Add($"planId={planId:D}");
        if (query.ProfileId is { } profile)
            parameters.Add($"profileId={Uri.EscapeDataString(profile)}");
        if (query.ProfileRevision is { } revision)
            parameters.Add("profileRevision=" + revision.ToString(CultureInfo.InvariantCulture));
        var requested = new UriBuilder(route) { Query = string.Join('&', parameters) }.Uri;
        return SendAsync<Wire.DeviceExportPlan, DeviceExportPlan>(new(HttpMethod.Get, requested), origin, "export-read", "export-read-host-response-mismatch",
            result => IsMatchingPlan(query, result) ? MapPlan(result) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPreview>> PreparePreviewAsync(PrepareDeviceExportPreview command, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportPreview>("export-preview-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.DeviceExportCommandRoutes.PreparePath);
        var request = new Wire.PrepareDeviceExportRequest(origin, context.OperationId, command.DeviceId, command.ProfileId,
            command.ProfileRevision, command.PlanId, command.ExpectedPlanRevision, command.LayoutEdits?.RelativePathsByTrack);
        return SendAsync<Wire.PrepareDeviceExportResponse, DeviceExportPreview>(new(HttpMethod.Post, route) { Content = JsonContent.Create(request) },
            origin, "export-preview", "export-preview-host-receipt-mismatch", result => MapPreview(command, context.OperationId, result), token);
    }

    /// <inheritdoc />
    public Task<Outcome<DeviceExportSubmission>> PublishAsync(PublishDeviceExport command, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<DeviceExportSubmission>("export-publish-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.DeviceExportCommandRoutes.PublishPath);
        Wire.ExportRemovalApproval? approval = command.RemovalApproval is { } value ? new(value.ProposalId, value.PlanId, value.PlanRevision) : null;
        var request = new Wire.PublishDeviceExportRequest(origin, context.OperationId, command.PlanId, command.ExpectedPlanRevision, approval);
        return SendAsync<Wire.DeviceExportCommandResult, DeviceExportSubmission>(new(HttpMethod.Post, route) { Content = JsonContent.Create(request) },
            origin, "export-publish", "export-publish-host-receipt-mismatch", result => MapPublication(command, context.OperationId, result), token);
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Read the durable FR-FILESYNC-001#ac-safety transfer projection.</remarks>
    public Task<Outcome<MediaTransferState>> ReadTransferAsync(GetMediaTransfer query, RequestContext context, CancellationToken token)
    {
        if (context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<MediaTransferState>("export-transfer-read-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, id => Wire.DeviceExportTransferRoutes.ReadPath(id, query.TransferId));
        return SendAsync<Wire.DeviceExportTransferCheckpoint, MediaTransferState>(new(HttpMethod.Get, route), origin, "export-transfer-read",
            "export-transfer-read-host-response-mismatch", result => MapTransfer(query.TransferId, result), token);
    }

    /// <inheritdoc />
    /// <remarks>2026-11-24: Replay the exact FR-FILESYNC-001#ac-safety retry receipt on an uncertain response.</remarks>
    public Task<Outcome<MediaTransferState>> RetryTransferAsync(ResumeMediaTransfer command, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<MediaTransferState>("export-transfer-retry-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, id => Wire.DeviceExportTransferRoutes.RetryPath(id, command.TransferId));
        var request = new Wire.RetryDeviceExportTransferRequest(origin, context.OperationId, command.ExpectedRevision);
        return SendAsync<Wire.RetryDeviceExportTransferResponse, MediaTransferState>(new(HttpMethod.Post, route) { Content = JsonContent.Create(request) },
            origin, "export-transfer-retry", "export-transfer-retry-host-receipt-mismatch", result =>
                result.OperationId == context.OperationId && result.Checkpoint is { Revision: > 0 } checkpoint &&
                checkpoint.Revision - 1 == command.ExpectedRevision ? MapTransfer(command.TransferId, checkpoint) : null, token);
    }

    private static MediaTransferState? MapTransfer(Guid transferId, Wire.DeviceExportTransferCheckpoint checkpoint)
    {
        if (transferId == Guid.Empty || checkpoint.TransferId != transferId || checkpoint.ShareId == Guid.Empty || checkpoint.Revision <= 0 ||
            checkpoint.BytesVerified < 0 || checkpoint.TotalBytes < 0 || checkpoint.BytesVerified > (checkpoint.TotalBytes ?? 0) ||
            checkpoint.FailureCode is { Length: 0 or > 100 } || checkpoint.Retryable && checkpoint.FailureCode is null)
            return null;
        TransferPhase? phase = checkpoint.Phase switch
        {
            Wire.DeviceExportTransferPhase.Queued => TransferPhase.Queued,
            Wire.DeviceExportTransferPhase.AwaitingReceiver => TransferPhase.Running,
            Wire.DeviceExportTransferPhase.Complete when checkpoint.TotalBytes == checkpoint.BytesVerified && checkpoint.FailureCode is null &&
                !checkpoint.Retryable => TransferPhase.Complete,
            Wire.DeviceExportTransferPhase.Paused => TransferPhase.Paused,
            Wire.DeviceExportTransferPhase.Failed when checkpoint.FailureCode is not null && !checkpoint.Retryable => TransferPhase.Failed,
            _ => null
        };
        return phase is not { } value ? null : new(checkpoint.TransferId, checkpoint.ShareId, checkpoint.Revision, value,
            checkpoint.BytesVerified, checkpoint.TotalBytes, checkpoint.FailureCode, checkpoint.Retryable);
    }

    private async Task<Outcome<TResult>> SendAsync<TWire, TResult>(HttpRequestMessage request, Guid origin, string operation,
        string mismatchCode, Func<TWire, TResult?> project, CancellationToken token) where TWire : class where TResult : class
    {
        using var message = request;
        var requested = message.RequestUri;
        try
        {
            var credential = await credentials.GetTokenAsync(libraryId, origin, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential))
                return new Outcome<TResult>.DependencyFailure($"{operation}-credentials-unavailable", true);
            message.Headers.Authorization = new("Bearer", credential);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != requested)
                return new Outcome<TResult>.DependencyFailure($"{operation}-endpoint-changed", false);
            if (!response.IsSuccessStatusCode)
                return await ReadFailureAsync<TResult>(response, operation, token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<TWire>(token).ConfigureAwait(false);
            return result is not null && project(result) is { } value ? new Outcome<TResult>.Success(value) :
                new Outcome<TResult>.DependencyFailure(mismatchCode, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<TResult>.Cancelled(); }
        catch (OperationCanceledException) { return new Outcome<TResult>.DependencyFailure($"{operation}-host-timeout", true); }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new Outcome<TResult>.DependencyFailure($"{operation}-host-unreachable", true);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new Outcome<TResult>.DependencyFailure($"invalid-{operation}-response", true);
        }
    }

    private static DeviceLayoutsSnapshot? MapLayouts(Guid deviceId, Wire.DeviceLayoutChoices choices)
    {
        if (choices.DeviceId != deviceId || choices.Profiles.IsDefault || !choices.Profiles.All(profile => profile is
            { ProfileId.Length: > 0, Revision: > 0, Name.Length: > 0, RelativePathTemplate.Length: > 0, SuggestedRoot.Length: > 1 }) ||
            choices.Profiles.Select(profile => profile.ProfileId).Distinct(StringComparer.Ordinal).Count() != choices.Profiles.Length ||
            choices.ActiveProfile is { } invalid && (string.IsNullOrWhiteSpace(invalid.ProfileId) || invalid.Revision <= 0))
            return null;
        return new(deviceId, [.. choices.Profiles.Select(profile => new DeviceProfile(profile.ProfileId, profile.Revision,
            profile.Name, profile.RelativePathTemplate, profile.PlaylistFormat, profile.SuggestedRoot))])
        {
            ActiveProfile = choices.ActiveProfile is { } active ? new(active.ProfileId, active.Revision) : null
        };
    }

    private static DeviceExportPlan MapPlan(Wire.DeviceExportPlan plan) => new(plan.PlanId, plan.PlanRevision, plan.DeviceId,
        plan.ProfileId, plan.ProfileRevision, [.. plan.Entries.Select(MapEntry)]);

    private static DeviceExportPreview? MapPreview(PrepareDeviceExportPreview command, Guid operation, Wire.PrepareDeviceExportResponse reply)
    {
        if (reply.OperationId != operation || reply.Plan is not { } plan || plan.PlanId is not { } planId || planId == Guid.Empty ||
            plan.PlanId != command.PlanId || plan.PlanRevision is not > 0 || plan.PlanRevision - 1 != command.ExpectedPlanRevision ||
            plan.DeviceId != command.DeviceId || plan.ProfileId != command.ProfileId || plan.ProfileRevision != command.ProfileRevision ||
            !ValidEntries(plan) || reply.Issues.IsDefault || reply.Changes.IsDefault || reply.ProposedRemovals.IsDefault ||
            reply.Issues.Any(issue => issue is not { Code.Length: > 0, Path.Length: > 0 }) || reply.Changes.Any(change => !ValidChange(change)) ||
            reply.ProposedRemovals.Any(string.IsNullOrWhiteSpace))
            return null;
        if (reply.Issues.IsEmpty && !reply.ProposedRemovals.SequenceEqual(reply.Changes
                .Where(change => change.Kind == Wire.ExportFileChangeKind.Remove || reply.ReassignsProfile && change.Kind == Wire.ExportFileChangeKind.Move)
                .Select(change => change.Before!)) || !reply.Issues.IsEmpty && !reply.ProposedRemovals.IsEmpty)
            return null;
        return new(MapPlan(plan), [.. reply.Issues.Select(issue => new ValidationIssue(issue.Code, issue.Path))], reply.ProposedRemovals)
        {
            Changes = [.. reply.Changes.Select(change => new ExportFileChange((ExportFileChangeKind)change.Kind, change.Before, change.After))]
        };
    }

    private static bool ValidChange(Wire.ExportFileChange? change) => change switch
    {
        { Kind: Wire.ExportFileChangeKind.Add, Before: null, After.Length: > 0 } => true,
        { Kind: Wire.ExportFileChangeKind.Move or Wire.ExportFileChangeKind.Replace, Before.Length: > 0, After.Length: > 0 } => true,
        { Kind: Wire.ExportFileChangeKind.Remove, Before.Length: > 0, After: null } => true,
        _ => false
    };

    private static DeviceExportSubmission? MapPublication(PublishDeviceExport command, Guid operation, Wire.DeviceExportCommandResult reply)
    {
        if (reply.OperationId != operation || reply.PlanId != command.PlanId || reply.PlanRevision != command.ExpectedPlanRevision)
            return null;
        return reply switch
        {
            Wire.DeviceExportCommandResult.Published committed when committed.PublicationId != Guid.Empty && committed.TransferId != Guid.Empty =>
                new DeviceExportSubmission.Published(new(committed.PublicationId, reply.PlanId, reply.PlanRevision, committed.TransferId)),
            Wire.DeviceExportCommandResult.ConfirmationRequired proposal when proposal.ProposalId != Guid.Empty &&
                !proposal.Paths.IsDefaultOrEmpty && !proposal.Paths.Any(string.IsNullOrWhiteSpace) =>
                new DeviceExportSubmission.ConfirmationRequired(new(proposal.ProposalId, reply.PlanId, reply.PlanRevision, proposal.Paths)),
            _ => null
        };
    }

    private static bool IsMatchingPlan(GetDeviceExportPlan query, Wire.DeviceExportPlan plan) =>
        plan.DeviceId == query.DeviceId && !string.IsNullOrWhiteSpace(plan.ProfileId) && plan.ProfileRevision > 0 &&
        (query.PlanId is null || query.PlanId == plan.PlanId) && (query.ProfileId is null || query.ProfileId == plan.ProfileId) &&
        (query.ProfileRevision is null || query.ProfileRevision == plan.ProfileRevision) && ValidEntries(plan) &&
        (plan.PlanId is { } id ? id != Guid.Empty && plan.PlanRevision is > 0 : plan.PlanRevision is null && plan.Entries.IsEmpty);

    private static bool Matches(StagePlaylistForDevice request, Wire.DeviceExportPlan plan) =>
        plan.DeviceId == request.DeviceId && plan.ProfileId == request.ProfileId && plan.ProfileRevision == request.ProfileRevision &&
        plan.PlanId is { } planId && planId != Guid.Empty && (request.PlanId is null || request.PlanId == planId) &&
        plan.PlanRevision is > 0 && plan.PlanRevision - 1 == (request.ExpectedPlanRevision ?? 0) && ValidEntries(plan) &&
        plan.Entries.OfType<Wire.ExportEntry.Playlist>().Any(entry => entry.PlaylistId == request.PlaylistId);

    private static bool MatchesEdit(Wire.DeviceExportPlan plan, Guid? priorId, long priorRevision) =>
        plan.DeviceId != Guid.Empty && !string.IsNullOrWhiteSpace(plan.ProfileId) && plan.ProfileRevision > 0 &&
        plan.PlanId is { } planId && planId != Guid.Empty && (priorId is null || priorId == planId) &&
        plan.PlanRevision is > 0 && plan.PlanRevision - 1 == priorRevision && ValidEntries(plan);

    private static bool ValidEntries(Wire.DeviceExportPlan plan) => !plan.Entries.IsDefault && plan.Entries.All(IsValidEntry) &&
        plan.Entries.Select(entry => entry.EntryId).Distinct().Count() == plan.Entries.Length;

    private static bool IsValidEntry(Wire.ExportEntry entry) => entry is { EntryId: var id, Destination.Length: > 0 } && id != Guid.Empty &&
        entry switch
        {
            Wire.ExportEntry.Audio audio => audio.TrackId != Guid.Empty && Enum.IsDefined(audio.Status),
            Wire.ExportEntry.Playlist playlist => playlist.PlaylistId != Guid.Empty && !string.IsNullOrWhiteSpace(playlist.Format) &&
                !playlist.Tracks.IsDefault && playlist.Tracks.All(track => track is { Destination.Length: > 0 } &&
                    track.TrackId != Guid.Empty && Enum.IsDefined(track.Status)),
            _ => false
        };

    private static ExportEntry MapEntry(Wire.ExportEntry entry) => entry switch
    {
        Wire.ExportEntry.Audio audio => new ExportEntry.Audio(audio.EntryId, audio.Destination, audio.TrackId, MapStatus(audio.Status)),
        Wire.ExportEntry.Playlist playlist => new ExportEntry.Playlist(playlist.EntryId, playlist.Destination, playlist.PlaylistId,
            playlist.Format, [.. playlist.Tracks.Select(track => new ExportTrackReference(track.TrackId, track.Destination, MapStatus(track.Status)))]),
        _ => throw new InvalidOperationException("Unexpected device export entry type.")
    };

    private static ExportAudioStatus MapStatus(Wire.ExportAudioStatus status) => status switch
    {
        Wire.ExportAudioStatus.Present => ExportAudioStatus.Present,
        Wire.ExportAudioStatus.Staged => ExportAudioStatus.Staged,
        Wire.ExportAudioStatus.Missing => ExportAudioStatus.Missing,
        Wire.ExportAudioStatus.Unavailable => ExportAudioStatus.Unavailable,
        _ => throw new InvalidOperationException("Unexpected device export audio status.")
    };

    private static async Task<Outcome<TResult>> ReadFailureAsync<TResult>(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Invalid<TResult>($"{operation}-access-denied");
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var problem = await response.Content.ReadFromJsonAsync<Wire.StageDevicePlaylistProblem>(cancellationToken).ConfigureAwait(false);
            if (problem?.Code is not { Length: > 0 and <= 100 } code)
                return new Outcome<TResult>.DependencyFailure($"invalid-{operation}-response", true);
            return response.StatusCode == HttpStatusCode.Conflict ? new Outcome<TResult>.Conflict(code, problem.ActualRevision) : Invalid<TResult>(code);
        }
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Content.Headers.ContentType?.MediaType == "application/json" &&
            await response.Content.ReadFromJsonAsync<Wire.StageDevicePlaylistProblem>(cancellationToken).ConfigureAwait(false) is
                { Code.Length: > 0 and <= 100 } unavailable)
            return new Outcome<TResult>.DependencyFailure(unavailable.Code, true);
        return new Outcome<TResult>.DependencyFailure($"{operation}-host-request-failed",
            (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
    }

    private static Outcome<TResult> Invalid<TResult>(string code) => new Outcome<TResult>.Invalid([new(code)]);
}
