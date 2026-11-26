using System.Collections.Immutable;
using System.Security.Cryptography;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Persistence;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises real private import copies, scoped receipts, and deletion after local revert.</summary>
/// <remarks>2026-11-24: Bind TEST-LIBRARY-004 and FR-BEETS-001#ac-import without an Avalonia screen.</remarks>
public sealed class LibraryBatchImportIntegrationTests
{
    [Fact]
    public async Task ImportCopiesStayPrivateAcrossRestartAndAreReleasedOnRevert()
    {
        var root = Directory.CreateTempSubdirectory("beets-batch-import-");
        try { await VerifyAsync(root.FullName, TestContext.Current.CancellationToken); }
        finally { root.Delete(recursive: true); }
    }

    private static async Task VerifyAsync(string root, CancellationToken token)
    {
        var scope = new LibraryBatchScope(Guid.NewGuid(), Guid.NewGuid());
        var request = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var database = Path.Combine(root, "desktop.db");
        var privateRoot = Path.Combine(root, "private");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var sources = Directory.CreateDirectory(Path.Combine(root, "sources")).FullName;
        var first = Path.Combine(sources, "Harbor Lights.flac");
        var second = Path.Combine(sources, "After Hours.flac");
        var firstBytes = new byte[] { 5, 4, 3, 2, 1 };
        var secondBytes = new byte[] { 9, 8, 7, 6 };
        var changedBytes = new byte[] { 0, 5, 0 };
        await File.WriteAllBytesAsync(first, firstBytes, token);
        await File.WriteAllBytesAsync(second, secondBytes, token);
        var host = new RecordingIntake(scope.LibraryId) { Sources = [first, second] };
        Assert.Throws<ArgumentException>(() => new FileLibraryBatchImportStage(Path.Combine(music, "batch"), music));
        var files = new FileLibraryBatchImportStage(privateRoot, music);
        UploadImportSources upload;
        LibraryBatchStageReceipt saved;
        bool receivedLocally;
        string deferred;
        bool failedCopyLeftNoEntry;
        var orphan = Guid.NewGuid();

        using (var store = LiteDbDesktopPlaylistStore.Open(database, allowCreate: true))
        {
            var (_, routes) = Build(store);
            var mode = Get(await routes.SendAsync(new SetLibraryBatchMode(true, 0), Context(), token));
            using var viewModel = new HostLibraryViewModel(new PresentationServices(routes, new InlineScheduler(), scope.OriginInstanceId),
                Guid.NewGuid(), scope.LibraryId);
            await viewModel.ReceiveBatchModeAsync(mode);
            await viewModel.PickSourcesCommand.ExecuteAsync(null);
            var selection = Assert.IsType<ImportSourceSelection>(viewModel.SourceSelection);
            upload = new(scope.LibraryId, selection.SelectionToken);
            Assert.True(viewModel.UploadCopiesCommand.CanExecute(null));
            await viewModel.UploadCopiesCommand.ExecuteAsync(null);
            saved = Assert.IsType<LibraryBatchStageReceipt>(viewModel.StagedChange);
            receivedLocally = viewModel.AcceptedJob is null && viewModel.SourceSelection is null && saved.Version.BatchId == mode.BatchId;
            Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first, token));

            deferred = Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(new StartImport(scope.LibraryId, Guid.NewGuid()), Context(), token)).Code;
            Assert.Equal(deferred, Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(
                new ChooseImportAction(Guid.NewGuid(), Guid.NewGuid(), new ImportAction.KeepTags(), 1), Context(), token)).Code);
            Assert.Equal(deferred, Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(
                new RetryLibraryJob(Guid.NewGuid(), 1), Context(), token)).Code);

            host.Sources = [first, Path.Combine(sources, "Missing.flac")];
            var incomplete = Get(await routes.SendAsync(new SelectImportSources(Guid.NewGuid()), Context(), token));
            Assert.IsType<Outcome<LibraryJobReceipt>.DependencyFailure>(await routes.SendAsync(
                new UploadImportSources(scope.LibraryId, incomplete.SelectionToken), Context(), token));
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, token));
            failedCopyLeftNoEntry = review.Entries.Length == 1 && Directory.GetFiles(privateRoot, "*.copy", SearchOption.AllDirectories).Length == 2;

            // Simulate an interruption after writing private bytes but before admitting the staged entry.
            var writer = (IWriteUnitOfWork<ILibraryBatchRepository>)store;
            Get(await writer.ExecuteAsync(repository =>
            {
                repository.ClaimImportCopies(scope, orphan);
                return new Outcome<bool>.Success(true);
            }, token));
            Assert.Single(Get(await files.CopyAsync(scope, orphan, [second], token)));
        }

        await File.WriteAllBytesAsync(first, changedBytes, token);
        using (var store = LiteDbDesktopPlaylistStore.Open(database, allowCreate: false))
        {
            var (batch, routes) = Build(store);
            Assert.True(Get(await routes.QueryAsync(new GetLibraryBatchMode(), request, token)).ImportCleanupPending);
            Assert.True(Get(await batch.RecoverPrivateImportCopiesAsync(token)));
            Assert.Equal(2, Directory.GetFiles(privateRoot, "*.copy", SearchOption.AllDirectories).Length);
            Assert.False(Get(await routes.QueryAsync(new GetLibraryBatchMode(), request, token)).ImportCleanupPending);
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), request, token));
            var entry = Assert.Single(review.Entries);
            var manifest = Assert.IsType<LibraryBatchChange.Import>(entry.Change);
            var manifestDigests = manifest.Files.Select(file => file.Sha256).ToArray();
            var pinnedHashes = manifestDigests.SequenceEqual([Convert.ToHexString(SHA256.HashData(firstBytes)), Convert.ToHexString(SHA256.HashData(secondBytes))]);
            var recovered = Get(await routes.SendAsync(upload, Context(saved.OperationId), token));
            Assert.Equal("operation-intent-mismatch", Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(
                upload with { SourceSelectionToken = Guid.NewGuid() }, Context(saved.OperationId), token)).Code);
            byte[] restored;
            await using (var copy = Get(await files.OpenVerifiedAsync(scope, entry.OperationId, manifest.Files[0], token)))
            {
                using var bytes = new MemoryStream();
                await copy.CopyToAsync(bytes, token);
                restored = bytes.ToArray();
            }

            var reverted = Get(await routes.SendAsync(new RevertLibraryBatch(review.Version), Context(), token));
            var copiesRemoved = !reverted.ImportCleanupPending && reverted.ChangeCount == 0 &&
                Directory.GetFiles(privateRoot, "*.copy", SearchOption.AllDirectories).Length == 0;
            var sourceReadback = await File.ReadAllBytesAsync(first, token);
            var secondReadback = await File.ReadAllBytesAsync(second, token);
            var originalsKept = changedBytes.SequenceEqual(sourceReadback) && secondBytes.SequenceEqual(secondReadback) &&
                Directory.GetFiles(music).Length == 0;
            var beforeDisable = host.Transfers;
            Get(await routes.SendAsync(new SetLibraryBatchMode(false, reverted.Revision), Context(), token));
            var delayed = Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(upload, Context(saved.OperationId), token));
            host.Sources = [first, second];
            var ordinarySelection = Get(await routes.SendAsync(new SelectImportSources(Guid.NewGuid()), Context(), token));
            var ordinary = Get(await routes.SendAsync(new UploadImportSources(scope.LibraryId, ordinarySelection.SelectionToken), Context(), token));

            LibraryBatchImportAssertions.Verify(new([.. manifest.Files.Select(file => file.Name)], beforeDisable, receivedLocally, pinnedHashes,
                recovered.LocalBatch == saved, firstBytes.SequenceEqual(restored), failedCopyLeftNoEntry, deferred, copiesRemoved, originalsKept,
                delayed.Code, host.Transfers, ordinary.JobId == host.JobId && ordinary.LocalBatch is null));
        }

        CommandContext Context(Guid? operation = null) => new(request, operation ?? Guid.NewGuid());

        (LibraryBatchHandlers, RequestDispatcher) Build(LiteDbDesktopPlaylistStore store)
        {
            var batch = new LibraryBatchHandlers(scope, store, host, files);
            return (batch, new ImportIntakeHandlers(scope.LibraryId, host, host, batch).Register(batch.Register(new())).Build());
        }
    }

    private static T Get<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            apply();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIntake(Guid library) : IImportSourcePicker, IImportIntakeHost, IApprovedLibraryBatchPublisher
    {
        public ImmutableArray<string> Sources { get; set; }
        public int Transfers { get; private set; }
        public Guid JobId { get; } = Guid.NewGuid();

        public Task<ImmutableArray<string>> PickFilesAsync(Guid ownerId, CancellationToken token) => Task.FromResult(Sources);

        public Task<Outcome<CompletedImportUpload>> CopyAsync(Guid libraryId, ImmutableArray<string> paths, CommandContext context, CancellationToken token)
        {
            Transfers++;
            Assert.Equal(library, libraryId);
            Assert.Equal(Sources.AsEnumerable(), paths);
            return Task.FromResult<Outcome<CompletedImportUpload>>(new Outcome<CompletedImportUpload>.Success(new(library, context.OperationId)));
        }

        public Task<Outcome<LibraryJobReceipt>> StartAsync(StartImport request, CommandContext context, CancellationToken token)
        {
            Transfers++;
            Assert.Equal(context.OperationId, request.UploadId);
            return Task.FromResult<Outcome<LibraryJobReceipt>>(new Outcome<LibraryJobReceipt>.Success(new(JobId, library, 1)));
        }

        public Task<Outcome<LibraryJobReceipt>> ChooseAsync(ChooseImportAction request, CommandContext context, CancellationToken token) => throw Unexpected();
        public Task<Outcome<LibraryJobReceipt>> RetryAsync(RetryLibraryJob request, CommandContext context, CancellationToken token) => throw Unexpected();
        public Task<Outcome<LibraryJobStatus>> InspectAsync(GetLibraryJob request, RequestContext context, CancellationToken token) => throw Unexpected();
        public Task<Outcome<TrackTagSnapshot>> ReadTagsAsync(GetTrackTags request, RequestContext context, CancellationToken token) => throw Unexpected();
        public Task<Outcome<LibraryJobReceipt>> EditTagsAsync(EditTrackTags request, CommandContext context, CancellationToken token) => throw Unexpected();
        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId, CancellationToken token) => throw Unexpected();

        private static InvalidOperationException Unexpected() => new("This scenario must not send another host request.");
    }
}
