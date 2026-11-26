using System.Collections.Immutable;
using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Submits one frozen desktop review to its registered library and waits for a terminal receipt through replay.</summary>
/// <remarks>2026-11-24: Retain a sealed approval when only admission or an uncertain reply is observed for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Send only missing verified private files after admission for FR-LIBRARY-002#ac-batch-local and ac-batch-commit.</remarks>
public sealed partial class HttpApprovedLibraryBatchPublisher(HttpClient client, Uri host, Guid library, IRegisteredHostTokenProvider credentials,
    ILibraryBatchImportFiles? privateImports = null) :
    IApprovedLibraryBatchPublisher, IApprovedBatchImportReviewGateway, IApprovedLibraryBatchWithdrawal
{
    private readonly Uri endpoint = RegisteredHostEndpoint.Resolve(host, library, Wire.ApprovedLibraryBatchRoutes.ApprovePath);

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchWithdrawalReceipt>> WithdrawAsync(LibraryBatchScope scope, LibraryBatchApproval approval, Guid operationId,
        CancellationToken token)
    {
        if (scope.LibraryId != library || scope.OriginInstanceId == Guid.Empty || operationId == Guid.Empty ||
            approval.Version.BatchId == Guid.Empty || approval.Version.Revision <= 0 || approval.ReviewId == Guid.Empty)
            return Task.FromResult<Outcome<LibraryBatchWithdrawalReceipt>>(new Outcome<LibraryBatchWithdrawalReceipt>.Invalid([new("invalid-library-batch-withdrawal")]));
        var address = RegisteredHostEndpoint.Resolve(host, library, id => Wire.ApprovedLibraryBatchRoutes.WithdrawPath(id, operationId));
        var command = new Wire.WithdrawApprovedLibraryBatchRequest(scope.OriginInstanceId, approval.Version.BatchId, approval.Version.Revision, approval.ReviewId);
        var message = new HttpRequestMessage(HttpMethod.Post, address) { Content = JsonContent.Create(command) };
        return RegisteredHostEndpoint.SendAsync<Wire.WithdrawApprovedLibraryBatchReceipt, LibraryBatchWithdrawalReceipt>(client, credentials, library,
            message, scope.OriginInstanceId, "library-batch-withdraw", "library-batch-withdrawal-mismatch", receipt =>
                receipt.LibraryId == scope.LibraryId && receipt.OriginInstanceId == scope.OriginInstanceId && receipt.ApprovalOperationId == operationId &&
                receipt.BatchId == command.BatchId && receipt.BatchRevision == command.BatchRevision && receipt.ReviewId == command.ReviewId
                    ? new(scope, approval, operationId) : null, token);
    }

    /// <inheritdoc />
    public async Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId,
        CancellationToken token)
    {
        if (scope.LibraryId != library || scope.OriginInstanceId == Guid.Empty || operationId == Guid.Empty ||
            approved.ReviewId is not { } reviewId || reviewId == Guid.Empty || approved.Entries.IsDefaultOrEmpty)
            return new Outcome<LibraryBatchCommitReceipt>.Invalid([new("invalid-approved-library-batch")]);
        var entries = ImmutableArray.CreateBuilder<Wire.ApprovedBatchEntry>(approved.Entries.Length);
        foreach (var entry in approved.Entries)
        {
            if (Project(entry.Change) is not { } change)
                return new Outcome<LibraryBatchCommitReceipt>.Invalid([new("invalid-approved-library-batch-entry")]);
            entries.Add(new(entry.OperationId, entry.Position, change));
        }
        var command = new Wire.ApproveLibraryBatchRequest(scope.OriginInstanceId, operationId, approved.Version.BatchId, approved.Version.Revision,
            reviewId, entries.MoveToImmutable());
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(command) };
        var sent = await RegisteredHostEndpoint.SendAsync<Wire.ApprovedLibraryBatchStatus, Wire.ApprovedLibraryBatchStatus>(client, credentials, library,
            request, scope.OriginInstanceId, "library-batch-submit", "library-batch-status-mismatch", status => Matches(status, command) ? status : null, token)
            .ConfigureAwait(false);
        if (sent is Outcome<Wire.ApprovedLibraryBatchStatus>.Success { Value: { Phase: Wire.BatchPublicationPhase.Accepted } admitted })
            return await TransferImportsAsync(scope, approved, admitted, token).ConfigureAwait(false);
        return sent switch
        {
            Outcome<Wire.ApprovedLibraryBatchStatus>.Success { Value: { Phase: Wire.BatchPublicationPhase.Published } status } =>
                new Outcome<LibraryBatchCommitReceipt>.Success(new(library, approved.Version, operationId, status.CatalogRevision!.Value,
                    [.. status.Playlists.Select(playlist => new LibraryBatchPublishedPlaylist(playlist.EntryOperationId, new(playlist.PlaylistId, playlist.PlaylistRevision)))])),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Success { Value: { Phase: Wire.BatchPublicationPhase.Running } } =>
                new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-publication-pending", true),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Success { Value: { Phase: Wire.BatchPublicationPhase.Rejected } rejected } =>
                new Outcome<LibraryBatchCommitReceipt>.Conflict(rejected.FailureCode!, null),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Invalid denied when denied.Issues.Any(issue => issue.Code == "library-batch-submit-access-denied") =>
                new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-outcome-unavailable", true),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Conflict { Code: "operation-intent-mismatch" } =>
                new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-outcome-mismatch", false),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Invalid error => new Outcome<LibraryBatchCommitReceipt>.Invalid(error.Issues),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Conflict error => new Outcome<LibraryBatchCommitReceipt>.Conflict(error.Code, error.ActualRevision),
            Outcome<Wire.ApprovedLibraryBatchStatus>.DependencyFailure error => new Outcome<LibraryBatchCommitReceipt>.DependencyFailure(error.Code, error.Retryable),
            Outcome<Wire.ApprovedLibraryBatchStatus>.Cancelled => new Outcome<LibraryBatchCommitReceipt>.Cancelled(),
            _ => throw new InvalidOperationException("Unexpected approved batch outcome.")
        };
    }

    private bool Matches(Wire.ApprovedLibraryBatchStatus response, Wire.ApproveLibraryBatchRequest command) => response.LibraryId == library &&
        response.OriginInstanceId == command.OriginInstanceId && response.OperationId == command.OperationId && response.BatchId == command.BatchId &&
        response.BatchRevision == command.BatchRevision && response.ReviewId == command.ReviewId && !response.Playlists.IsDefault &&
        MatchesTransfers(response, command) &&
        (response.Phase switch
        {
            Wire.BatchPublicationPhase.Accepted => Pending(response) && response.FailureCode is null,
            Wire.BatchPublicationPhase.Running => Pending(response) && response.FailureCode is null && response.TransferredImports.Length == response.ImportPartCount,
            Wire.BatchPublicationPhase.Rejected => Pending(response) && !string.IsNullOrWhiteSpace(response.FailureCode),
            Wire.BatchPublicationPhase.Published => response.FailureCode is null && PublishedRevisionMatches(response, command) &&
                response.TransferredImports.Length == response.ImportPartCount &&
                response.Playlists.All(item => item is not null && item.PlaylistId != Guid.Empty && item.PlaylistRevision > 0) &&
                response.Playlists.Select(item => item.EntryOperationId).SequenceEqual(command.Entries
                    .Where(entry => entry.Change is Wire.ApprovedBatchChange.Playlist).Select(entry => entry.OperationId)),
            _ => false
        });

    /// <summary>Allows an empty feed only when every approved entry is confirmed as skipped.</summary>
    /// <remarks>2026-11-24: Settle skip-only approvals under FR-LIBRARY-002#ac-batch-commit without publishing a revision.</remarks>
    private static bool PublishedRevisionMatches(Wire.ApprovedLibraryBatchStatus status, Wire.ApproveLibraryBatchRequest command) =>
        status.CatalogRevision > 0 || (status.CatalogRevision == 0 && !command.Entries.IsEmpty && !status.ImportReviews.IsDefault &&
            status.ImportReviews.Length == command.Entries.Length && command.Entries.Zip(status.ImportReviews).All(pair =>
                pair.First.Change is Wire.ApprovedBatchChange.Import && pair.Second is { Choice: Wire.ImportChoiceKind.Skip } review &&
                pair.First.Position == review.Position && pair.First.OperationId == review.EntryOperationId));

    /// <summary>Checks that native work has no published effect for FR-LIBRARY-002#ac-batch-commit.</summary>
    private static bool Pending(Wire.ApprovedLibraryBatchStatus status) => status.CatalogRevision is null && status.Playlists.IsEmpty;

    private static bool MatchesTransfers(Wire.ApprovedLibraryBatchStatus status, Wire.ApproveLibraryBatchRequest command)
    {
        if (status.TransferredImports.IsDefault || status.ImportPartCount != command.Entries.Select(entry => entry.Change)
                .OfType<Wire.ApprovedBatchChange.Import>().Sum(change => change.Files.Length))
            return false;
        var seen = new HashSet<(int, int)>();
        foreach (var copy in status.TransferredImports)
        {
            if (copy is null || copy.LibraryId != status.LibraryId || copy.OriginInstanceId != command.OriginInstanceId ||
                copy.ApprovalOperationId != command.OperationId || copy.Position < 1 || copy.Position > command.Entries.Length ||
                !seen.Add((copy.Position, copy.Index)))
                return false;
            var entry = command.Entries[copy.Position - 1];
            if (entry.OperationId != copy.EntryOperationId || entry.Change is not Wire.ApprovedBatchChange.Import files ||
                copy.Index < 0 || copy.Index >= files.Files.Length || files.Files[copy.Index].FileId != copy.FileId)
                return false;
        }
        return true;
    }

    private async Task<Outcome<LibraryBatchCommitReceipt>> TransferImportsAsync(LibraryBatchScope scope, LibraryBatchReview approved,
        Wire.ApprovedLibraryBatchStatus admitted, CancellationToken token)
    {
        if (admitted.TransferredImports.Length < admitted.ImportPartCount)
        {
            if (privateImports is null)
                return new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("private-import-stage-unavailable", true);
            var received = admitted.TransferredImports.Select(copy => (copy.Position, copy.Index)).ToHashSet();
            foreach (var entry in approved.Entries)
            {
                if (entry.Change is not LibraryBatchChange.Import import)
                    continue;
                for (var index = 0; index < import.Files.Length; index++)
                {
                    if (received.Contains((entry.Position, index)))
                        continue;
                    var file = import.Files[index];
                    var opened = await privateImports.OpenVerifiedAsync(scope, entry.OperationId, file, token).ConfigureAwait(false);
                    if (opened is not Outcome<Stream>.Success { Value: var source })
                        return TransferFailure(opened);
                    await using var verified = source;
                    var address = RegisteredHostEndpoint.Resolve(host, library, id => Wire.ApprovedLibraryBatchRoutes.ImportPartPath(id,
                        admitted.OperationId, entry.Position, index, file.FileId));
                    var body = new StreamContent(verified);
                    body.Headers.ContentType = new("application/octet-stream");
                    body.Headers.ContentLength = file.Size;
                    var request = new HttpRequestMessage(HttpMethod.Put, address) { Content = body };
                    var expected = new Wire.ApprovedBatchImportPartReceipt(library, scope.OriginInstanceId, admitted.OperationId, entry.OperationId,
                        entry.Position, index, file.FileId);
                    var copied = await RegisteredHostEndpoint.SendAsync<Wire.ApprovedBatchImportPartReceipt, Wire.ApprovedBatchImportPartReceipt>(client,
                        credentials, library, request, scope.OriginInstanceId, "library-batch-import", "batch-import-copy-mismatch",
                        copy => copy == expected ? copy : null, token).ConfigureAwait(false);
                    if (copied is not Outcome<Wire.ApprovedBatchImportPartReceipt>.Success)
                        return TransferFailure(copied);
                }
            }
        }
        return new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-publication-pending", true);
    }

    private static Outcome<LibraryBatchCommitReceipt> TransferFailure<T>(Outcome<T> outcome) => outcome switch
    {
        Outcome<T>.Cancelled => new Outcome<LibraryBatchCommitReceipt>.Cancelled(),
        Outcome<T>.DependencyFailure failure => new Outcome<LibraryBatchCommitReceipt>.DependencyFailure(failure.Code, failure.Retryable),
        Outcome<T>.Invalid failure => new Outcome<LibraryBatchCommitReceipt>.DependencyFailure(failure.Issues.FirstOrDefault()?.Code ?? "batch-import-copy-unavailable", true),
        Outcome<T>.Conflict failure => new Outcome<LibraryBatchCommitReceipt>.DependencyFailure(failure.Code, true),
        _ => throw new InvalidOperationException("A failed verified transfer was expected.")
    };

    private static Wire.ApprovedBatchChange? Project(LibraryBatchChange change) => change switch
    {
        LibraryBatchChange.TrackTags tags => new Wire.ApprovedBatchChange.TrackTags(tags.TrackId, tags.ExpectedRevision, tags.Before, tags.Changes),
        LibraryBatchChange.Import import => new Wire.ApprovedBatchChange.Import([.. import.Files.Select(file =>
            new Wire.ApprovedBatchImportFile(file.FileId, new(file.Name, file.Size, file.Sha256)))]),
        LibraryBatchChange.Organize { Plan: var plan } when !plan.TrackIds.IsDefaultOrEmpty => new Wire.ApprovedBatchChange.Maintenance(new(plan.PlanId,
            plan.LibraryId, Wire.LibraryMaintenanceKind.Organize, plan.CatalogRevision, plan.TrackIds,
            [.. plan.Moves.Select(move => new Wire.MaintenancePathChange(move.TrackId, move.Source, move.Destination))], []), false),
        LibraryBatchChange.Remove { Plan: var plan, Approval: var approval } when plan.PlanId == approval.PlanId && plan.Mode == approval.Mode =>
            new Wire.ApprovedBatchChange.Maintenance(new(plan.PlanId, plan.LibraryId, plan.Mode == RemovalMode.CatalogAndAudio
                ? Wire.LibraryMaintenanceKind.RemoveAudio : Wire.LibraryMaintenanceKind.RemoveCatalog, plan.CatalogRevision, plan.TrackIds, [], plan.AudioPaths),
                plan.Mode == RemovalMode.CatalogAndAudio),
        LibraryBatchChange.Playlist playlist => new Wire.ApprovedBatchChange.Playlist(playlist.QueueSource is { } queue
            ? new(Wire.PlaylistSaveSourceKind.Queue, queue.SessionId) : new(Wire.PlaylistSaveSourceKind.Draft, playlist.DraftId),
            playlist.QueueSource?.QueueRevision ?? playlist.DraftRevision, playlist.BasePlaylist?.PlaylistId, playlist.BasePlaylist?.Revision, playlist.Name,
            [.. playlist.Entries.Select(entry => new Wire.PlaylistEntryData(entry.EntryId, entry.TrackId))]),
        _ => null
    };
}
