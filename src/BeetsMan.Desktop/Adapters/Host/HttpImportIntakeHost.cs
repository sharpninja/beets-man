using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Streams the selected desktop files to the registered library and validates every receipt.</summary>
/// <remarks>2026-11-24: Bind the desktop upload and job commands to HTTPS for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Expose the hosted album review and send the exact choice for FR-BEETS-001#ac-import.</remarks>
/// <remarks>2026-11-24: Route the same job and operation through guided recovery for FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Read and submit track tags through registered HTTPS for FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class HttpImportIntakeHost(HttpClient client, Uri host, Guid libraryId, IRegisteredHostTokenProvider credentials) : IImportIntakeHost
{
    /// <inheritdoc />
    public async Task<Outcome<CompletedImportUpload>> CopyAsync(Guid requestedLibrary, ImmutableArray<string> paths, CommandContext context,
        CancellationToken token)
    {
        if (requestedLibrary != libraryId || context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Invalid<CompletedImportUpload>("import-upload-scope-invalid");
        if (paths.IsDefaultOrEmpty || paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            return Invalid<CompletedImportUpload>("invalid-import-source-selection");

        try
        {
            var files = ImmutableArray.CreateBuilder<Wire.ImportUploadFile>(paths.Length);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                await using var source = Open(path);
                var bytes = source.Length;
                var sha = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
                if (source.Position != bytes || source.Length != bytes)
                    return Invalid<CompletedImportUpload>("import-source-changed");
                files.Add(new(Path.GetFileName(path), bytes, Convert.ToHexString(sha)));
            }

            var uploadId = context.OperationId;
            var completion = new CompletedImportUpload(libraryId, uploadId);
            var manifest = new Wire.BeginImportUploadRequest(origin, uploadId, files.MoveToImmutable());
            var begin = await SendAsync<Wire.ImportUploadReceipt, CompletedImportUpload>(HttpMethod.Post, Wire.ImportIntakeRoutes.BeginPath,
                JsonContent.Create(manifest), origin, "import-upload", response => response.UploadId == uploadId ? completion : null, token).ConfigureAwait(false);
            if (begin is not Outcome<CompletedImportUpload>.Success)
                return begin;

            for (var index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                await using var source = Open(paths[index]);
                if (source.Length != manifest.Files[index].Size)
                    return Invalid<CompletedImportUpload>("import-source-changed");
                var body = new StreamContent(source);
                body.Headers.ContentType = new("application/octet-stream");
                body.Headers.ContentLength = manifest.Files[index].Size;
                var copy = await SendAsync<Wire.ImportUploadPartReceipt, CompletedImportUpload>(HttpMethod.Put,
                    id => Wire.ImportIntakeRoutes.PartPath(id, uploadId, index), body, origin, "import-copy",
                    response => response.UploadId == uploadId && response.Index == index ? completion : null, token).ConfigureAwait(false);
                if (copy is not Outcome<CompletedImportUpload>.Success)
                    return copy;
            }
            return new Outcome<CompletedImportUpload>.Success(completion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<CompletedImportUpload>.Cancelled(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new Outcome<CompletedImportUpload>.DependencyFailure("import-source-unavailable", true); }
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> StartAsync(StartImport request, CommandContext context, CancellationToken token)
    {
        if (request.LibraryId != libraryId || request.UploadId == Guid.Empty || context.OperationId == Guid.Empty ||
            context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<LibraryJobReceipt>("import-start-scope-invalid"));
        var body = new Wire.StartImportRequest(origin, context.OperationId, request.UploadId);
        return SendAsync<Wire.StartImportReceipt, LibraryJobReceipt>(HttpMethod.Post, Wire.ImportIntakeRoutes.StartPath, JsonContent.Create(body),
            origin, "import-start", response => response.OperationId == context.OperationId && response.LibraryId == libraryId &&
                response.JobId != Guid.Empty && response.Revision > 0 ? new(response.JobId, response.LibraryId, response.Revision) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> ChooseAsync(ChooseImportAction request, CommandContext context, CancellationToken token)
    {
        if (request.JobId == Guid.Empty || request.ReviewItemId == Guid.Empty || request.ExpectedRevision <= 0 ||
            context.OperationId == Guid.Empty || context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<LibraryJobReceipt>("import-choice-scope-invalid"));
        var choice = request.Choice switch
        {
            ImportAction.UseMatch match when match.MatchId != Guid.Empty => (Wire.ImportChoiceKind.UseMatch, (Guid?)match.MatchId),
            ImportAction.KeepTags => (Wire.ImportChoiceKind.KeepTags, (Guid?)null),
            ImportAction.Skip => (Wire.ImportChoiceKind.Skip, (Guid?)null),
            _ => (Wire.ImportChoiceKind.Unspecified, (Guid?)null)
        };
        if (choice.Item1 == Wire.ImportChoiceKind.Unspecified)
            return Task.FromResult(Invalid<LibraryJobReceipt>("invalid-import-choice"));
        var body = new Wire.ChooseImportRequest(origin, context.OperationId, request.ReviewItemId, request.ExpectedRevision, choice.Item1, choice.Item2);
        return SendAsync<Wire.ChooseImportReceipt, LibraryJobReceipt>(HttpMethod.Post, id => Wire.ImportIntakeRoutes.ChoicePath(id, request.JobId),
            JsonContent.Create(body), origin, "import-choice", response =>
                response.OperationId == context.OperationId && response.LibraryId == libraryId && response.JobId == request.JobId &&
                response.Revision > request.ExpectedRevision ? new(response.JobId, response.LibraryId, response.Revision) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobStatus>> InspectAsync(GetLibraryJob request, RequestContext context, CancellationToken token)
    {
        if (request.JobId == Guid.Empty || context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<LibraryJobStatus>("library-job-scope-invalid"));
        return SendAsync<Wire.ImportJobSnapshot, LibraryJobStatus>(HttpMethod.Get, id => Wire.ImportIntakeRoutes.JobPath(id, request.JobId),
            null, origin, "library-job-read", response => response.JobId == request.JobId && response.LibraryId == libraryId && response.Revision > 0
                ? MapJob(response) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<TrackTagSnapshot>> ReadTagsAsync(GetTrackTags request, RequestContext context, CancellationToken token)
    {
        if (request.TrackId == Guid.Empty || context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<TrackTagSnapshot>("track-tags-scope-invalid"));
        return SendAsync<Wire.TrackTagsSnapshot, TrackTagSnapshot>(HttpMethod.Get, id => Wire.TrackTagRoutes.Path(id, request.TrackId),
            null, origin, "track-tags-read", response => response.TrackId == request.TrackId && response.CatalogRevision > 0 &&
                response.Tags is not null && response.Tags.Values.All(value => value is not null)
                ? new(response.TrackId, response.CatalogRevision, response.Tags) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> EditTagsAsync(EditTrackTags request, CommandContext context, CancellationToken token)
    {
        if (request.TrackId == Guid.Empty || request.ExpectedCatalogRevision <= 0 || request.TagChanges is not { Count: > 0 } ||
            context.OperationId == Guid.Empty || context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<LibraryJobReceipt>("track-tag-edit-scope-invalid"));
        var body = new Wire.EditTrackTagsRequest(origin, context.OperationId, request.ExpectedCatalogRevision, request.TagChanges);
        return SendAsync<Wire.TrackTagEditReceipt, LibraryJobReceipt>(HttpMethod.Post, id => Wire.TrackTagRoutes.Path(id, request.TrackId),
            JsonContent.Create(body), origin, "track-tag-edit", response => response.OperationId == context.OperationId &&
                response.LibraryId == libraryId && response.JobId != Guid.Empty && response.Revision > 0
                ? new(response.JobId, response.LibraryId, response.Revision) : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> RetryAsync(RetryLibraryJob request, CommandContext context, CancellationToken token)
    {
        if (request.JobId == Guid.Empty || request.ExpectedRevision <= 0 || context.OperationId == Guid.Empty ||
            context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<LibraryJobReceipt>("import-retry-scope-invalid"));
        var body = new Wire.RetryImportRequest(origin, context.OperationId, request.ExpectedRevision);
        return SendAsync<Wire.RetryImportReceipt, LibraryJobReceipt>(HttpMethod.Post, id => Wire.ImportIntakeRoutes.RetryPath(id, request.JobId),
            JsonContent.Create(body), origin, "import-retry", response =>
                response.OperationId == context.OperationId && response.LibraryId == libraryId && response.JobId == request.JobId &&
                response.Revision > request.ExpectedRevision ? new(response.JobId, response.LibraryId, response.Revision) : null, token);
    }

    private Task<Outcome<TResult>> SendAsync<TWire, TResult>(HttpMethod method, Func<Guid, string> path, HttpContent? content, Guid origin,
        string operation, Func<TWire, TResult?> map, CancellationToken token) where TWire : class where TResult : class
    {
        var endpoint = RegisteredHostEndpoint.Resolve(host, libraryId, path);
        var message = new HttpRequestMessage(method, endpoint) { Content = content };
        return RegisteredHostEndpoint.SendAsync(client, credentials, libraryId, message, origin, operation, $"{operation}-receipt-mismatch", map, token);
    }

    private static LibraryJobStatus? MapJob(Wire.ImportJobSnapshot job)
    {
        var phase = job.Phase switch
        {
            Wire.ImportJobPhase.Accepted => LibraryJobPhase.Accepted,
            Wire.ImportJobPhase.Running => LibraryJobPhase.Running,
            Wire.ImportJobPhase.ReviewRequired => LibraryJobPhase.ReviewRequired,
            Wire.ImportJobPhase.Published => LibraryJobPhase.Published,
            Wire.ImportJobPhase.Failed => LibraryJobPhase.Failed,
            Wire.ImportJobPhase.Skipped => LibraryJobPhase.Skipped,
            _ => (LibraryJobPhase?)null
        };
        var review = MapReview(job.Review);
        return phase is { } valid && (valid == LibraryJobPhase.Published ? job.CatalogRevision is > 0 : job.CatalogRevision is null) &&
            (job.FailureCode is null or { Length: > 0 and <= 100 }) && (!job.Retryable || valid == LibraryJobPhase.Failed) &&
            (valid == LibraryJobPhase.ReviewRequired ? review is not null : job.Review is null)
            ? new(job.JobId, job.LibraryId, job.Revision, valid, review, job.CatalogRevision, job.FailureCode, job.Retryable) : null;
    }

    /// <summary>Validates and maps the same public review in direct and batched guided imports.</summary>
    internal static ImportReviewItem? MapReview(Wire.ImportAlbumReview? review) =>
        review is { } offered && offered.ItemId != Guid.Empty && !offered.SourceNames.IsDefaultOrEmpty &&
        offered.SourceNames.All(name => !string.IsNullOrWhiteSpace(name)) && !offered.Matches.IsDefault &&
        offered.Matches.All(match => match is { } && match.MatchId != Guid.Empty && !string.IsNullOrWhiteSpace(match.Title) && match.Artist is not null &&
            (match.Comparison is null || ValidComparison(match.Comparison, offered.SourceNames.Length))) &&
        offered.Matches.Select(match => match.MatchId).Distinct().Count() == offered.Matches.Length
            ? new(offered.ItemId, [.. offered.Matches.Select(MapMatch)], offered.SourceNames)
            : null;

    private static bool ValidComparison(Wire.ImportMatchComparison comparison, int sourceCount) =>
        !comparison.Tracks.IsDefault && comparison.Tracks.Length == sourceCount && (comparison.ReleaseTrackCount is null or > 0) &&
        comparison.Tracks.All(track => track is not null && !string.IsNullOrWhiteSpace(track.OriginalTitle) &&
            (track.ProposedTitle is null ? !comparison.CanApply : !string.IsNullOrWhiteSpace(track.ProposedTitle))) &&
        comparison.Tracks.Select(track => track.SourceIndex).SequenceEqual(Enumerable.Range(0, sourceCount));

    private static ImportMatch MapMatch(Wire.ImportReviewMatch match) => new(match.MatchId, match.Title, match.Artist,
        match.Comparison is { } comparison ? new(comparison.ReleaseDate, comparison.Country, comparison.ReleaseTrackCount, comparison.CanApply,
            [.. comparison.Tracks.Select(track => new ImportTrackTitleComparison(track.SourceIndex, track.OriginalTitle, track.ProposedTitle))]) : null);

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);
}
