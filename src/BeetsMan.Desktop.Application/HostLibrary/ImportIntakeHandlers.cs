using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Application.HostLibrary;

/// <summary>Obtains the owning desktop scope's approved local file paths.</summary>
/// <remarks>2026-11-24: Keep the file picker behind a port for FR-BEETS-001#ac-import.</remarks>
public interface IImportSourcePicker
{
    /// <summary>Returns an empty selection when the operator cancels the picker.</summary>
    Task<ImmutableArray<string>> PickFilesAsync(Guid ownerId, CancellationToken token);
}

/// <summary>Identifies copies committed under a single host upload.</summary>
public sealed record CompletedImportUpload(Guid LibraryId, Guid UploadId);

/// <summary>Stages copies and inspects or submits jobs across the registered library boundary.</summary>
public interface IImportIntakeHost
{
    /// <summary>Copies every selected source and returns only after the last copy is acknowledged.</summary>
    Task<Outcome<CompletedImportUpload>> CopyAsync(Guid libraryId, ImmutableArray<string> paths, CommandContext context, CancellationToken token);

    /// <summary>Submits the completed upload with the original command identity.</summary>
    Task<Outcome<LibraryJobReceipt>> StartAsync(StartImport request, CommandContext context, CancellationToken token);

    /// <summary>Submits the operator's choice for the exact hosted album review revision.</summary>
    /// <remarks>2026-11-24: Bind album approval for FR-BEETS-001#ac-import and ac-recover.</remarks>
    Task<Outcome<LibraryJobReceipt>> ChooseAsync(ChooseImportAction request, CommandContext context, CancellationToken token);

    /// <summary>Schedules recovery of the same failed guided import at the displayed revision.</summary>
    Task<Outcome<LibraryJobReceipt>> RetryAsync(RetryLibraryJob request, CommandContext context, CancellationToken token);

    /// <summary>Reads the registered desktop's job.</summary>
    Task<Outcome<LibraryJobStatus>> InspectAsync(GetLibraryJob request, RequestContext context, CancellationToken token);

    /// <summary>Reads the current tags and item revision for a track in the registered library.</summary>
    /// <remarks>2026-11-24: Bind FR-BEETS-001#ac-edit.</remarks>
    Task<Outcome<TrackTagSnapshot>> ReadTagsAsync(GetTrackTags request, RequestContext context, CancellationToken token);

    /// <summary>Submits the draft against the revision used to open it.</summary>
    /// <remarks>2026-11-24: Bind FR-BEETS-001#ac-edit and ac-recover.</remarks>
    Task<Outcome<LibraryJobReceipt>> EditTagsAsync(EditTrackTags request, CommandContext context, CancellationToken token);
}

/// <summary>Dispatches the desktop import commands without exposing picker paths or publishing catalog changes.</summary>
/// <remarks>2026-11-24: Bind STB08 selection, upload, acceptance and job lookup for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Register the approved album choice for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Dispatch recoverable import jobs for FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Dispatch selected track tags and revision-pinned drafts for FR-BEETS-001#ac-edit.</remarks>
/// <remarks>2026-11-24: Capture tag edits in the desktop batch for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
/// <remarks>2026-11-24: Stage local import bytes and guard hosted jobs for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
/// <remarks>2026-11-25: Report an unavailable selector in the headless composition under FR-BEETS-001#ac-import while retaining tag and job handlers.</remarks>
public sealed partial class ImportIntakeHandlers(Guid libraryId, IImportSourcePicker? picker, IImportIntakeHost host,
    LibraryBatchHandlers? batches = null, ICachedTrackTags? cachedTags = null)
    : ICommandHandler<SelectImportSources, ImportSourceSelection>, ICommandHandler<UploadImportSources, LibraryJobReceipt>,
      ICommandHandler<StartImport, LibraryJobReceipt>, ICommandHandler<ChooseImportAction, LibraryJobReceipt>,
      ICommandHandler<RetryLibraryJob, LibraryJobReceipt>, IQueryHandler<GetLibraryJob, LibraryJobStatus>,
      IQueryHandler<GetTrackTags, TrackTagSnapshot>, ICommandHandler<EditTrackTags, LibraryJobReceipt>
{
    private readonly Dictionary<Guid, SelectedSources> selections = [];

    /// <summary>Registers host library intake, track tag and job requests on the normal dispatcher.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterCommand<SelectImportSources, ImportSourceSelection>(this)
        .RegisterCommand<UploadImportSources, LibraryJobReceipt>(this)
        .RegisterCommand<StartImport, LibraryJobReceipt>(this)
        .RegisterCommand<ChooseImportAction, LibraryJobReceipt>(this)
        .RegisterCommand<RetryLibraryJob, LibraryJobReceipt>(this)
        .RegisterQuery<GetLibraryJob, LibraryJobStatus>(this)
        .RegisterQuery<GetTrackTags, TrackTagSnapshot>(this)
        .RegisterCommand<EditTrackTags, LibraryJobReceipt>(this);

    /// <inheritdoc />
    public async Task<Outcome<ImportSourceSelection>> HandleAsync(SelectImportSources request, CommandContext context, CancellationToken token)
    {
        if (request.OwnerId == Guid.Empty || context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return new Outcome<ImportSourceSelection>.Invalid([new("import-selection-owner-required")]);
        if (picker is null)
            return new Outcome<ImportSourceSelection>.DependencyFailure("import-source-picker-unavailable", true);
        var files = await picker.PickFilesAsync(request.OwnerId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (files.IsDefaultOrEmpty)
            return new Outcome<ImportSourceSelection>.Cancelled();
        if (files.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetFileName(path))))
            return new Outcome<ImportSourceSelection>.Invalid([new("invalid-import-source-selection")]);
        var selectionToken = Guid.NewGuid();
        lock (selections)
        {
            foreach (var existing in selections.Where(entry => entry.Value.OwnerId == request.OwnerId && entry.Value.Origin == origin).ToArray())
                selections.Remove(existing.Key);
            selections.Add(selectionToken, new(request.OwnerId, origin, files));
        }
        return new Outcome<ImportSourceSelection>.Success(new(selectionToken, [.. files.Select(path => Path.GetFileName(path))]));
    }

    /// <inheritdoc />
    public async Task<Outcome<LibraryJobReceipt>> HandleAsync(UploadImportSources request, CommandContext context, CancellationToken token)
    {
        if (request.LibraryId != libraryId || context.OperationId == Guid.Empty || request.SourceSelectionToken == Guid.Empty ||
            context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return SelectionUnavailable();

        Outcome<LibraryJobReceipt> result;
        try
        {
            result = batches is null ? await UploadDirectAsync(request, context, token).ConfigureAwait(false) :
                await batches.RouteMutationAsync<LibraryBatchChange.Import, LibraryJobReceipt>(context,
                    ct => UploadDirectAsync(request, context, ct),
                    ct => WithSelectedSourcesAsync<LibraryBatchChange.Import>(request.SourceSelectionToken, context,
                        selection => batches.PrepareImportCopiesAsync(context.OperationId, request.SourceSelectionToken, selection.Files, ct), ct),
                    staged => staged.SelectionToken == request.SourceSelectionToken,
                    (_, stage) => new(Guid.Empty, libraryId, 0, stage), token).ConfigureAwait(false);
        }
        finally
        {
            if (batches is not null)
                await batches.RecoverPrivateImportCopiesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        if (result is Outcome<LibraryJobReceipt>.Success)
        {
            lock (selections)
                selections.Remove(request.SourceSelectionToken);
        }
        return result;
    }

    private Task<Outcome<LibraryJobReceipt>> UploadDirectAsync(UploadImportSources request, CommandContext context, CancellationToken token) =>
        WithSelectedSourcesAsync<LibraryJobReceipt>(request.SourceSelectionToken, context, async selection =>
        {
            if (selection.Completed is null)
            {
                var copied = await host.CopyAsync(libraryId, selection.Files, context, token).ConfigureAwait(false);
                if (copied is not Outcome<CompletedImportUpload>.Success { Value: var completed })
                    return CopyFailure(copied);
                if (completed.LibraryId != libraryId || completed.UploadId != context.OperationId)
                    return new Outcome<LibraryJobReceipt>.DependencyFailure("import-upload-receipt-mismatch", true);
                selection.Completed = completed;
            }
            var started = await host.StartAsync(new StartImport(libraryId, selection.Completed.UploadId), context, token).ConfigureAwait(false);
            if (started is Outcome<LibraryJobReceipt>.Success)
            {
                lock (selections)
                    selections.Remove(request.SourceSelectionToken);
            }
            return started;
        }, token);

    private async Task<Outcome<T>> WithSelectedSourcesAsync<T>(Guid selectionToken, CommandContext context,
        Func<SelectedSources, Task<Outcome<T>>> use, CancellationToken token)
    {
        var origin = context.Request.OriginInstanceId!.Value;
        if (Find(selectionToken, origin) is not { } selection)
            return SelectionUnavailable<T>();
        await selection.Lane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Find(selectionToken, origin) != selection)
                return SelectionUnavailable<T>();
            if (selection.OperationId is { } existing && existing != context.OperationId)
                return new Outcome<T>.Conflict("import-selection-operation-changed", null);
            selection.OperationId = context.OperationId;
            return await use(selection).ConfigureAwait(false);
        }
        finally { selection.Lane.Release(); }
    }

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(StartImport request, CommandContext context, CancellationToken token) =>
        request.LibraryId == libraryId ? RunHostedJobAsync(context, ct => host.StartAsync(request, context, ct), token) : Task.FromResult(SelectionUnavailable());

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(ChooseImportAction request, CommandContext context, CancellationToken token) =>
        RunHostedJobAsync(context, ct => host.ChooseAsync(request, context, ct), token);

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(RetryLibraryJob request, CommandContext context, CancellationToken token) =>
        RunHostedJobAsync(context, ct => host.RetryAsync(request, context, ct), token);

    private Task<Outcome<LibraryJobReceipt>> RunHostedJobAsync(CommandContext context, Func<CancellationToken, Task<Outcome<LibraryJobReceipt>>> send,
        CancellationToken token) => batches is null ? send(token) : batches.RunHostedJobAsync(context.Request, send, token);

    /// <inheritdoc />
    public Task<Outcome<LibraryJobStatus>> HandleAsync(GetLibraryJob request, RequestContext context, CancellationToken token) =>
        host.InspectAsync(request, context, token);

    /// <inheritdoc />
    public Task<Outcome<TrackTagSnapshot>> HandleAsync(GetTrackTags request, RequestContext context, CancellationToken token) =>
        batches is null ? host.ReadTagsAsync(request, context, token) : ReadWithBatchAsync(batches, request, context, token);

    /// <inheritdoc />
    public Task<Outcome<LibraryJobReceipt>> HandleAsync(EditTrackTags request, CommandContext context, CancellationToken token) =>
        batches is null ? host.EditTagsAsync(request, context, token) : batches.RouteMutationAsync<LibraryBatchChange.TrackTags, LibraryJobReceipt>(context,
            ct => host.EditTagsAsync(request, context, ct), ct => PrepareTagEditAsync(batches, request, context.Request, ct),
            stored => stored.TrackId == request.TrackId && stored.ExpectedRevision == request.ExpectedCatalogRevision &&
                SameTags(stored.Changes, request.TagChanges), (_, stage) => new(Guid.Empty, libraryId, 0, stage), token);

    private SelectedSources? Find(Guid selectionToken, Guid origin)
    {
        lock (selections)
            return selections.GetValueOrDefault(selectionToken) is { } selection && selection.Origin == origin ? selection : null;
    }

    private static Outcome<LibraryJobReceipt> SelectionUnavailable() => SelectionUnavailable<LibraryJobReceipt>();
    private static Outcome<T> SelectionUnavailable<T>() => new Outcome<T>.Invalid([new("import-source-selection-unavailable")]);

    private static Outcome<LibraryJobReceipt> CopyFailure(Outcome<CompletedImportUpload> failure) => failure switch
    {
        Outcome<CompletedImportUpload>.Invalid value => new Outcome<LibraryJobReceipt>.Invalid(value.Issues),
        Outcome<CompletedImportUpload>.Conflict value => new Outcome<LibraryJobReceipt>.Conflict(value.Code, value.ActualRevision),
        Outcome<CompletedImportUpload>.DependencyFailure value => new Outcome<LibraryJobReceipt>.DependencyFailure(value.Code, value.Retryable),
        Outcome<CompletedImportUpload>.Cancelled => new Outcome<LibraryJobReceipt>.Cancelled(),
        _ => throw new InvalidOperationException("An upload completion was expected.")
    };

    /// <summary>Keeps the admitted operation and completion boundary until the owning desktop receives its job.</summary>
    private sealed class SelectedSources(Guid ownerId, Guid origin, ImmutableArray<string> files)
    {
        public Guid OwnerId { get; } = ownerId;
        public Guid Origin { get; } = origin;
        public ImmutableArray<string> Files { get; } = files;
        public SemaphoreSlim Lane { get; } = new(1, 1);
        public Guid? OperationId { get; set; }
        public CompletedImportUpload? Completed { get; set; }
    }
}
