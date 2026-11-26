using System.Collections.Immutable;
using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Reads private guided reviews and submits their exact choices over the registered HTTPS host.</summary>
/// <remarks>2026-11-24: Verify batch and entry scope for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-import.</remarks>
public sealed partial class HttpApprovedLibraryBatchPublisher
{
    /// <inheritdoc />
    public Task<Outcome<LibraryBatchImportReviews>> ReadImportReviewsAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid approval,
        CancellationToken token)
    {
        if (scope.LibraryId != library || scope.OriginInstanceId == Guid.Empty || approval == Guid.Empty || approved.Entries.IsDefault || approved.ReviewId is null)
            return Task.FromResult<Outcome<LibraryBatchImportReviews>>(new Outcome<LibraryBatchImportReviews>.Invalid([new("invalid-batch-import-review")]));
        var address = RegisteredHostEndpoint.Resolve(host, library, id => Wire.ApprovedLibraryBatchRoutes.StatusPath(id, approval));
        return RegisteredHostEndpoint.SendAsync<Wire.ApprovedLibraryBatchStatus, LibraryBatchImportReviews>(client, credentials, library,
            new(HttpMethod.Get, address), scope.OriginInstanceId, "batch-import-review", "batch-import-review-mismatch",
            reply => ReadReviews(reply, scope, approved, approval), token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryBatchImportChoiceReceipt>> ChooseImportAsync(LibraryBatchScope scope, Guid approval, ChooseLibraryBatchImportAction choice,
        Guid operation, CancellationToken token)
    {
        var (kind, match) = choice.Action switch
        {
            ImportAction.KeepTags => (Wire.ImportChoiceKind.KeepTags, (Guid?)null),
            ImportAction.Skip => (Wire.ImportChoiceKind.Skip, (Guid?)null),
            ImportAction.UseMatch selected when selected.MatchId != Guid.Empty => (Wire.ImportChoiceKind.UseMatch, (Guid?)selected.MatchId),
            _ => (Wire.ImportChoiceKind.Unspecified, (Guid?)null)
        };
        if (scope.LibraryId != library || scope.OriginInstanceId == Guid.Empty || approval == Guid.Empty || operation == Guid.Empty ||
            choice.EntryOperationId == Guid.Empty || choice.ReviewItemId == Guid.Empty || choice.Position <= 0 || choice.ExpectedRevision is <= 0 or long.MaxValue ||
            kind == Wire.ImportChoiceKind.Unspecified)
            return Task.FromResult<Outcome<LibraryBatchImportChoiceReceipt>>(new Outcome<LibraryBatchImportChoiceReceipt>.Invalid([new("invalid-batch-import-choice")]));

        var command = new Wire.ChooseApprovedBatchImportRequest(scope.OriginInstanceId, operation, choice.EntryOperationId, choice.ReviewItemId,
            choice.ExpectedRevision, kind, match);
        var address = RegisteredHostEndpoint.Resolve(host, library, id => Wire.ApprovedLibraryBatchRoutes.ImportChoicePath(id, approval, choice.Position));
        return RegisteredHostEndpoint.SendAsync<Wire.ApprovedBatchImportChoiceReceipt, LibraryBatchImportChoiceReceipt>(client, credentials, library,
            new(HttpMethod.Post, address) { Content = JsonContent.Create(command) }, scope.OriginInstanceId, "batch-import-choice", "batch-import-choice-mismatch",
            reply => reply.LibraryId == library && reply.OriginInstanceId == scope.OriginInstanceId && reply.ApprovalOperationId == approval &&
                reply.EntryOperationId == choice.EntryOperationId && reply.ReviewItemId == choice.ReviewItemId && reply.OperationId == operation &&
                reply.Position == choice.Position && reply.Revision == choice.ExpectedRevision + 1 && reply.Choice == kind && reply.MatchId == match
                ? new(choice.Version, approval, reply.Position, reply.EntryOperationId, reply.OperationId, reply.Revision, choice.Action) : null, token);
    }

    /// <summary>Accepts only ordered reviews tied to the original local entries and file names.</summary>
    private static LibraryBatchImportReviews? ReadReviews(Wire.ApprovedLibraryBatchStatus reply, LibraryBatchScope scope, LibraryBatchReview approved, Guid approval)
    {
        if (reply.LibraryId != scope.LibraryId || reply.OriginInstanceId != scope.OriginInstanceId || reply.OperationId != approval ||
            reply.BatchId != approved.Version.BatchId || reply.BatchRevision != approved.Version.Revision || reply.ReviewId != approved.ReviewId)
            return null;
        var entries = ImmutableArray.CreateBuilder<LibraryBatchImportReview>();
        if (reply.ImportReviews.IsDefault)
            return new(approved.Version, approval, []);
        var previous = 0;
        foreach (var review in reply.ImportReviews)
        {
            if (review is null || review.Position <= previous || review.Position > approved.Entries.Length || review.Revision <= 0 ||
                approved.Entries[review.Position - 1] is not { Change: LibraryBatchChange.Import import, OperationId: var operation } || operation != review.EntryOperationId ||
                HttpImportIntakeHost.MapReview(review.Album) is not { } album || !album.SourceNames.SequenceEqual(import.Files.Select(file => file.Name)))
                return null;
            var action = review.Choice switch
            {
                Wire.ImportChoiceKind.KeepTags when review.MatchId is null => (ImportAction)new ImportAction.KeepTags(),
                Wire.ImportChoiceKind.Skip when review.MatchId is null => new ImportAction.Skip(),
                Wire.ImportChoiceKind.UseMatch when review.MatchId is { } id && album.Matches.Any(match => match.MatchId == id && match.Details?.CanApply == true)
                    => new ImportAction.UseMatch(id),
                _ => null
            };
            if (action is null && (review.Choice is not null || review.MatchId is not null))
                return null;
            entries.Add(new(review.Position, operation, review.Revision, album, action));
            previous = review.Position;
        }
        return new(approved.Version, approval, entries.ToImmutable());
    }
}
