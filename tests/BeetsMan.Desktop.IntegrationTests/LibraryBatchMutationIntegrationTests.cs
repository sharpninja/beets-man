using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies that real tag and playlist CQRS handlers obey the desktop batch boundary.</summary>
/// <remarks>2026-11-24: Exercise cached reads, exact review, and local revert for TEST-LIBRARY-004.</remarks>
/// <remarks>2026-11-26: Check private catalog and media changes and their restoration for FR-LIBRARY-002#ac-batch-local and FR-LIBRARY-002#ac-batch-revert.</remarks>
public sealed class LibraryBatchMutationIntegrationTests
{
    [Fact]
    public async Task TagAndNamedPlaylistCommandsStayLocalAndTheirEffectsRevert()
    {
        var directory = Directory.CreateTempSubdirectory("beets-batch-mutations-");
        try { await VerifyAsync(directory.FullName, TestContext.Current.CancellationToken); }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifyAsync(string root, CancellationToken token)
    {
        var path = Path.Combine(root, "desktop.db");
        using (LiteDbDesktopPlaylistStore.Open(path, allowCreate: true)) { }
        var scope = new LibraryBatchScope(Guid.NewGuid(), Guid.NewGuid());
        var origin = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var track = Guid.NewGuid();
        var secondTrack = Guid.NewGuid();
        using (var database = new LiteDatabase(path))
        {
            database.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
            {
                ["_id"] = track, ["LibraryId"] = scope.LibraryId, ["Title"] = "Harbor Lights", ["SourceRevision"] = 42L,
                ["Genre"] = "Ambient", ["RelativePath"] = "Harbor Lights.flac", ["Year"] = 2023
            });
        }

        var host = new RecordingHost(scope.LibraryId);
        var tagRequest = new EditTrackTags(track, ImmutableDictionary<string, string?>.Empty.Add("Genre", "Downtempo"), 42);
        var tagAttempt = Context();
        var saveAttempt = Context();
        LibraryJobReceipt stagedTag;
        PlaylistSavedReceipt stagedPlaylist;
        SavePlaylist saveRequest;
        TrackTagSnapshot cachedTags;
        TrackTagSnapshot stagedTags;
        TrackTagSnapshot restoredTags;
        string[] entries;
        int hostReads;
        int tagWrites;
        int playlistWrites;
        string closedTag;
        string closedPlaylist;
        bool draftUnchanged;
        bool matchingReplay;
        bool ordinaryPlaylist;

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var routes = Build(store);
            Get(await routes.SendAsync(new SetLibraryBatchMode(true, 0), Context(), token));
            cachedTags = Get(await routes.QueryAsync(new GetTrackTags(track), origin, token));
            var stale = await routes.SendAsync(tagRequest with { ExpectedCatalogRevision = 41 }, Context(), token);
            Assert.Equal(42, Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(stale).ActualRevision);
            var invalid = tagRequest with { TagChanges = tagRequest.TagChanges.Add("year", "12.5") };
            Assert.Contains(Assert.IsType<Outcome<LibraryJobReceipt>.Invalid>(await routes.SendAsync(invalid, Context(), token)).Issues,
                issue => issue.Code == "invalid-track-tag-edit");
            stagedTag = Get(await routes.SendAsync(tagRequest, tagAttempt, token));
            stagedTags = Get(await routes.QueryAsync(new GetTrackTags(track), origin, token));

            var draft = Get(await routes.SendAsync(new CreatePlaylistDraft(new PlaylistDraftSource.Empty()), Context(), token));
            draft = Get(await routes.SendAsync(new EditPlaylistDraft(draft.DraftId, new PlaylistDraftEdit.Append([track, secondTrack]), draft.Revision),
                Context(), token));
            saveRequest = new(draft.DraftId, "Night drive", draft.Revision, null);
            stagedPlaylist = Get(await routes.SendAsync(saveRequest, saveAttempt, token));
            var readDraft = Get(await routes.QueryAsync(new GetPlaylistDraft(draft.DraftId), origin, token));
            draftUnchanged = readDraft.Name.Length == 0 && readDraft.BasePlaylist is null && readDraft.Revision == draft.Revision &&
                readDraft.Entries.SequenceEqual(draft.Entries);
        }

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var routes = Build(store);
            var review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), origin, token));
            var tagDraft = Assert.IsType<LibraryBatchChange.TrackTags>(review.Entries[0].Change);
            var playlist = Assert.IsType<LibraryBatchChange.Playlist>(review.Entries[1].Change);
            entries = [$"{tagDraft.Before["genre"]} → {tagDraft.Changes["genre"]}", $"{playlist.Name}:{playlist.Entries.Length}"];
            Assert.Equal([track, secondTrack], playlist.Entries.Select(entry => entry.TrackId));
            var replay = Get(await routes.SendAsync(tagRequest with { TagChanges = tagRequest.TagChanges.Remove("Genre").Add("genre", "Downtempo") },
                tagAttempt, token));
            var playlistReplay = Get(await routes.SendAsync(saveRequest, saveAttempt, token));
            matchingReplay = replay == stagedTag && playlistReplay == stagedPlaylist;
            Assert.Equal("operation-intent-mismatch", Assert.IsType<Outcome<PlaylistSavedReceipt>.Conflict>(await routes.SendAsync(
                saveRequest with { Name = "Changed" }, saveAttempt, token)).Code);
            using var editor = new TagEditViewModel(new PresentationServices(routes, new InlineBatchScheduler(), scope.OriginInstanceId), Guid.NewGuid());
            await editor.OpenCommand.ExecuteAsync(track);
            editor.Changes = ImmutableDictionary<string, string?>.Empty.Add("year", "2024");
            await editor.SaveCommand.ExecuteAsync(null);
            Assert.Equal(review.Version.BatchId, editor.StagedEdit?.Version.BatchId);
            Assert.Null(editor.AcceptedJob);
            review = Get(await routes.QueryAsync(new GetLibraryBatchReview(), origin, token));
            Get(await routes.SendAsync(new RevertLibraryBatch(review.Version), Context(), token));
            await editor.OpenCommand.ExecuteAsync(track);
            Assert.Null(editor.StagedEdit);
            Assert.Empty(editor.Changes);
            Assert.Equal("2023", editor.Track?.Tags["year"]);
            restoredTags = Get(await routes.QueryAsync(new GetTrackTags(track), origin, token));
            hostReads = host.TagReads;
            tagWrites = host.TagWrites;
            playlistWrites = host.PlaylistWrites;
            var mode = Get(await routes.QueryAsync(new GetLibraryBatchMode(), origin, token));
            Get(await routes.SendAsync(new SetLibraryBatchMode(false, mode.Revision), Context(), token));
            closedTag = Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(tagRequest, tagAttempt, token)).Code;
            closedPlaylist = Assert.IsType<Outcome<PlaylistSavedReceipt>.Conflict>(await routes.SendAsync(saveRequest, saveAttempt, token)).Code;

            var directTagAttempt = Context();
            var directSaveAttempt = Context();
            var ordinaryTag = Get(await routes.SendAsync(tagRequest, directTagAttempt, token));
            Assert.Null(ordinaryTag.LocalBatch);
            Assert.NotEqual(Guid.Empty, ordinaryTag.JobId);
            var ordinary = Get(await routes.SendAsync(saveRequest, directSaveAttempt, token));
            ordinaryPlaylist = ordinary.LocalBatch is null && ordinary.PlaylistId == host.PlaylistId && ordinary.Revision == 1;

            mode = Get(await routes.QueryAsync(new GetLibraryBatchMode(), origin, token));
            Get(await routes.SendAsync(new SetLibraryBatchMode(true, mode.Revision), Context(), token));
            Assert.Equal("library-batch-operation-routed-directly", Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await routes.SendAsync(
                tagRequest, directTagAttempt, token)).Code);
            Assert.Equal("library-batch-operation-routed-directly", Assert.IsType<Outcome<PlaylistSavedReceipt>.Conflict>(await routes.SendAsync(
                saveRequest, directSaveAttempt, token)).Code);
            Assert.Empty(Get(await routes.QueryAsync(new GetLibraryBatchReview(), origin, token)).Entries);
        }

        LibraryBatchMutationAssertions.Verify(new(cachedTags.Tags["genre"], stagedTags.Tags["genre"], restoredTags.Tags["genre"],
            stagedTags.CatalogRevision, stagedTag.LocalBatch?.OperationId == tagAttempt.OperationId && stagedTag.JobId == Guid.Empty,
            stagedPlaylist.LocalBatch?.OperationId == saveAttempt.OperationId && stagedPlaylist.PlaylistId == Guid.Empty,
            entries, hostReads, tagWrites, playlistWrites, closedTag, closedPlaylist, draftUnchanged, matchingReplay,
            host.TagWrites, host.PlaylistWrites, ordinaryPlaylist));

        CommandContext Context() => new(origin, Guid.NewGuid());

        RequestDispatcher Build(LiteDbDesktopPlaylistStore store)
        {
            var catalog = new LiteDbCatalogReader(store, scope.LibraryId, root, () => true);
            var batch = new LibraryBatchHandlers(scope, store, host);
            var dispatcher = batch.Register(new());
            new ImportIntakeHandlers(scope.LibraryId, host, host, batch, catalog).Register(dispatcher);
            return new PlaylistWorkspaceHandlers(store, store, catalog, host, scope.OriginInstanceId, batch).Register(dispatcher).Build();
        }
    }

    private static T Get<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    private sealed class InlineBatchScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            apply();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHost(Guid library) : IImportIntakeHost, IImportSourcePicker, IPlaylistPublicationHost, IApprovedLibraryBatchPublisher
    {
        public Guid PlaylistId { get; } = Guid.NewGuid();
        public int TagReads { get; private set; }
        public int TagWrites { get; private set; }
        public int PlaylistWrites { get; private set; }

        public Task<Outcome<TrackTagSnapshot>> ReadTagsAsync(GetTrackTags request, RequestContext context, CancellationToken token)
        {
            TagReads++;
            return Task.FromResult<Outcome<TrackTagSnapshot>>(new Outcome<TrackTagSnapshot>.Success(new(request.TrackId, 42,
                ImmutableDictionary<string, string>.Empty.Add("genre", "Ambient"))));
        }

        public Task<Outcome<LibraryJobReceipt>> EditTagsAsync(EditTrackTags request, CommandContext context, CancellationToken token)
        {
            TagWrites++;
            return Task.FromResult<Outcome<LibraryJobReceipt>>(new Outcome<LibraryJobReceipt>.Success(new(Guid.NewGuid(), library, 1)));
        }

        public Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken token)
        {
            PlaylistWrites++;
            return Task.FromResult<Outcome<PlaylistSavedReceipt>>(new Outcome<PlaylistSavedReceipt>.Success(new(operation.DraftId, PlaylistId, 1)));
        }

        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview review, Guid operationId, CancellationToken token) =>
            throw new InvalidOperationException("This scenario should never approve publication.");

        public Task<ImmutableArray<string>> PickFilesAsync(Guid ownerId, CancellationToken token) => throw UnexpectedImport();
        public Task<Outcome<CompletedImportUpload>> CopyAsync(Guid libraryId, ImmutableArray<string> paths, CommandContext context, CancellationToken token) =>
            throw UnexpectedImport();
        public Task<Outcome<LibraryJobReceipt>> StartAsync(StartImport request, CommandContext context, CancellationToken token) => throw UnexpectedImport();
        public Task<Outcome<LibraryJobReceipt>> ChooseAsync(ChooseImportAction request, CommandContext context, CancellationToken token) => throw UnexpectedImport();
        public Task<Outcome<LibraryJobReceipt>> RetryAsync(RetryLibraryJob request, CommandContext context, CancellationToken token) => throw UnexpectedImport();
        public Task<Outcome<LibraryJobStatus>> InspectAsync(GetLibraryJob request, RequestContext context, CancellationToken token) => throw UnexpectedImport();

        private static InvalidOperationException UnexpectedImport() => new("Import is outside this test scenario.");
    }
}
