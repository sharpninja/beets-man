using System.Collections.Immutable;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises the typed local batch lifecycle against a real desktop store across reopen.</summary>
/// <remarks>2026-11-24: Bind local persistence and the exact publisher request for TEST-LIBRARY-004.</remarks>
public sealed class LibraryBatchLifecycleIntegrationTests
{
    /// <summary>Checks that malformed publication receipts leave the original approval and entries available for retry.</summary>
    /// <remarks>2026-11-26: Exercise persisted receipt rejection and exact replay for FR-LIBRARY-002#ac-batch-commit.</remarks>
    [Fact]
    public async Task ForeignOrIncompleteCommitReceiptRetainsSealAndEntries()
    {
        var root = Directory.CreateTempSubdirectory("beets-library-batch-receipt-");
        try { await CheckReceiptAsync(Path.Combine(root.FullName, "desktop.db"), TestContext.Current.CancellationToken); }
        finally { root.Delete(recursive: true); }
    }

    /// <summary>Feeds malformed acknowledgments into the same persisted approval, then replays the valid result.</summary>
    /// <remarks>2026-11-26: Verify the retained entries and operation identity for FR-LIBRARY-002#ac-batch-commit.</remarks>
    private static async Task CheckReceiptAsync(string path, CancellationToken token)
    {
        var scope = new LibraryBatchScope(Guid.NewGuid(), Guid.NewGuid());
        var origin = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var publication = new CommandContext(origin, Guid.NewGuid());
        var publisher = new ReplaceablePublisher();
        var playlistId = Guid.NewGuid();
        var playlist = new LibraryBatchChange.Playlist(Guid.NewGuid(), 1, new(playlistId, 3), "Evening", []);
        LibraryBatchReview review;
        LibraryBatchCommitReceipt expected;
        CommitLibraryBatch command;

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var batch = new LibraryBatchHandlers(scope, store, publisher);
            var mode = Get(await batch.HandleAsync(new GetLibraryBatchMode(), origin, token));
            Get(await batch.HandleAsync(new SetLibraryBatchMode(true, mode.Revision), Next(), token));
            var named = Get(await batch.StageAsync(playlist, Next(), token));
            var tag = new LibraryBatchChange.TrackTags(Guid.NewGuid(), 42,
                ImmutableSortedDictionary<string, string>.Empty.Add("genre", "Jazz"),
                ImmutableSortedDictionary<string, string?>.Empty.Add("genre", "Ambient"));
            Get(await batch.StageAsync(tag, Next(), token));
            review = Get(await batch.HandleAsync(new GetLibraryBatchReview(), origin, token));
            command = new(new(review.Version, review.ReviewId!.Value), KeepModeEnabled: false);
            expected = new(scope.LibraryId, review.Version, publication.OperationId, 43, [new(named.OperationId, new(playlistId, 4))]);
        }

        var valid = Assert.Single(expected.Playlists);
        LibraryBatchCommitReceipt[] invalid =
        [
            expected with { Playlists = default },
            expected with { Playlists = [valid, valid] },
            expected with { Playlists = [valid, valid with { EntryOperationId = Guid.NewGuid() }] },
            expected with { Playlists = [valid with { EntryOperationId = review.Entries[1].OperationId }] },
            expected with { Playlists = [valid with { Version = new(Guid.NewGuid(), 4) }] },
            expected with { Playlists = [valid with { Version = new(playlistId, 3) }] },
            expected with { LibraryId = Guid.NewGuid() },
            expected with { OperationId = Guid.NewGuid() },
            expected with { Version = review.Version with { BatchId = Guid.NewGuid() } },
            expected with { Version = review.Version with { Revision = review.Version.Revision + 1 } },
            expected with { CatalogRevision = 42 }
        ];
        for (var index = 0; index < invalid.Length; index++)
        {
            using var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
            var batch = new LibraryBatchHandlers(scope, store, publisher);
            publisher.Receipt = invalid[index];
            var outcome = index == 0 ? await batch.HandleAsync(command, publication, token) :
                await batch.HandleAsync(new ResumeLibraryBatchCommit(), Next(), token);
            var failure = Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(outcome);
            var mode = Get(await batch.HandleAsync(new GetLibraryBatchMode(), origin, token));
            var retained = Get(await batch.HandleAsync(new GetLibraryBatchReview(), origin, token));
            LibraryBatchLifecycleAssertions.VerifyRejectedReceipt(new(failure.Code, failure.Retryable, mode.IsCommitting,
                retained.ReviewId, retained.Version.Revision, [.. retained.Entries.Select(entry => entry.OperationId)]),
                review.ReviewId!.Value, review.Version.Revision, [.. review.Entries.Select(entry => entry.OperationId)]);
        }

        publisher.Receipt = expected;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var batch = new LibraryBatchHandlers(scope, store, publisher);
            var receipt = Get(await batch.HandleAsync(new ResumeLibraryBatchCommit(), Next(), token));
            Assert.Equal(expected.Playlists.AsEnumerable(), receipt.Playlists.AsEnumerable());
            Assert.Equal(expected.CatalogRevision, receipt.CatalogRevision);
            var mode = Get(await batch.HandleAsync(new GetLibraryBatchMode(), origin, token));
            var replayed = Get(await batch.HandleAsync(command, publication, token));
            LibraryBatchLifecycleAssertions.VerifyReceiptRecovery(new(mode.IsCommitting, mode.ChangeCount, publisher.Operations.Count,
                [.. publisher.Operations], receipt.OperationId, JsonSerializer.Serialize(receipt) == JsonSerializer.Serialize(replayed)),
                publication.OperationId, invalid.Length + 1);
        }

        CommandContext Next() => new(origin, Guid.NewGuid());
    }

    [Fact]
    public async Task ReviewRevertAndUncertainApprovalSurviveDesktopRestart()
    {
        var root = Directory.CreateTempSubdirectory("beets-library-batch-");
        try { await CheckAsync(Path.Combine(root.FullName, "desktop.db"), TestContext.Current.CancellationToken); }
        finally { root.Delete(recursive: true); }
    }

    private static async Task CheckAsync(string path, CancellationToken token)
    {
        var scope = new LibraryBatchScope(Guid.NewGuid(), Guid.NewGuid());
        var origin = new RequestContext(Guid.NewGuid(), scope.OriginInstanceId);
        var trackId = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var track = new LibraryBatchChange.TrackTags(trackId, 42, ImmutableSortedDictionary<string, string>.Empty.Add("genre", "Ambient"),
            ImmutableSortedDictionary<string, string?>.Empty.Add("genre", "Downtempo"));
        var playlist = new LibraryBatchChange.Playlist(Guid.NewGuid(), 5, new PlaylistVersion(playlistId, 41), "Evening",
            [new PlaylistEntry(Guid.NewGuid(), trackId)]);
        var autosave = playlist with { DraftId = Guid.Empty, DraftRevision = 0, QueueSource = new(Guid.NewGuid(), 3, "fixture-intent", null, true) };
        var trackAttempt = Context();
        var publisher = new RecordingPublisher();
        LibraryBatchMode initial;
        LibraryBatchReview listed;
        LibraryBatchStageReceipt originalStage;
        bool replayed;
        bool afterRestart;
        string dirtyDisable;
        string stale;
        int beforeTransfer;
        int revertTransfer;
        LibraryBatchMode reverted;
        string closedStage;
        LibraryBatchReview sealedReview;
        string lostReply;
        string revertBlocked;
        string otherCommit;
        var commitContext = Context();
        CommitLibraryBatch commit;

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var (batches, dispatcher) = Build(store);
            initial = Get(await dispatcher.QueryAsync(new GetLibraryBatchMode(), origin, token));
            Get(await dispatcher.SendAsync(new SetLibraryBatchMode(true, initial.Revision), Context(), token));
            originalStage = Get(await batches.StageAsync(track, trackAttempt, token));
            Get(await batches.StageAsync(playlist, Context(), token));
            var firstReview = Get(await dispatcher.QueryAsync(new GetLibraryBatchReview(), origin, token));
            var duplicate = Get(await batches.StageAsync(track, trackAttempt, token));
            var afterDuplicate = Get(await dispatcher.QueryAsync(new GetLibraryBatchReview(), origin, token));
            replayed = duplicate == originalStage && afterDuplicate.Version == firstReview.Version && afterDuplicate.ReviewId == firstReview.ReviewId;
            Get(await batches.StageAsync(autosave, Context(), token));
            stale = Assert.IsType<Outcome<LibraryBatchCommitReceipt>.Conflict>(await dispatcher.SendAsync(
                new CommitLibraryBatch(new(firstReview.Version, firstReview.ReviewId!.Value)), Context(), token)).Code;
            beforeTransfer = publisher.Calls.Count;
        }

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var (batches, dispatcher) = Build(store);
            var mode = Get(await dispatcher.QueryAsync(new GetLibraryBatchMode(), origin, token));
            afterRestart = mode.IsEnabled && mode.ChangeCount == 3;
            var impostor = origin with { OriginInstanceId = Guid.NewGuid() };
            Assert.IsType<Outcome<LibraryBatchReview>.Invalid>(await dispatcher.QueryAsync(new GetLibraryBatchReview(), impostor, token));
            dirtyDisable = Assert.IsType<Outcome<LibraryBatchMode>.Conflict>(await dispatcher.SendAsync(new SetLibraryBatchMode(false, mode.Revision), Context(), token)).Code;
            listed = Get(await dispatcher.QueryAsync(new GetLibraryBatchReview(), origin, token));
            var storedTrack = Assert.IsType<LibraryBatchChange.TrackTags>(listed.Entries[0].Change);
            Assert.Equal("Ambient", storedTrack.Before["genre"]);
            Assert.Equal("Downtempo", storedTrack.Changes["genre"]);
            Assert.Equal(playlist.Entries, Assert.IsType<LibraryBatchChange.Playlist>(listed.Entries[1].Change).Entries.AsEnumerable());
            Assert.Equal(autosave.QueueSource, Assert.IsType<LibraryBatchChange.Playlist>(listed.Entries[2].Change).QueueSource);

            reverted = Get(await dispatcher.SendAsync(new RevertLibraryBatch(listed.Version), Context(), token));
            revertTransfer = publisher.Calls.Count;
            closedStage = Assert.IsType<Outcome<LibraryBatchStageReceipt>.Conflict>(await batches.StageAsync(track, trackAttempt, token)).Code;
            Assert.NotEqual(listed.Version.BatchId, reverted.BatchId);
            var organize = new OrganizePlan(Guid.NewGuid(), scope.LibraryId, 42, [new("Library/Harbor.flac", "Library/Ambient/Harbor.flac")]);
            var removal = new RemovalPlan(Guid.NewGuid(), scope.LibraryId, 42, RemovalMode.CatalogAndAudio, [trackId], ["Library/Harbor.flac"]);
            Get(await batches.StageAsync(new LibraryBatchChange.Import([new(Guid.NewGuid(), "Another.flac", 256, new string('F', 64))]), Context(), token));
            Get(await batches.StageAsync(new LibraryBatchChange.Organize(organize), Context(), token));
            Get(await batches.StageAsync(new LibraryBatchChange.Remove(removal, new(removal.PlanId, removal.Mode)), Context(), token));
            sealedReview = Get(await dispatcher.QueryAsync(new GetLibraryBatchReview(), origin, token));
            Assert.Equal(["Import", "Organize", "Remove audio"], sealedReview.Entries.Select(Describe));
            var unreviewed = new CommitLibraryBatch(new(sealedReview.Version, Guid.NewGuid()));
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.Conflict>(await dispatcher.SendAsync(unreviewed, Context(), token));
            Assert.Empty(publisher.Calls);
            commit = new(new(sealedReview.Version, sealedReview.ReviewId!.Value), KeepModeEnabled: false);
            lostReply = Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(await dispatcher.SendAsync(commit, commitContext, token)).Code;
        }

        LibraryBatchMode completed;
        LibraryBatchCommitReceipt receipt;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var (batches, dispatcher) = Build(store);
            var pending = Get(await dispatcher.QueryAsync(new GetLibraryBatchMode(), origin, token));
            Assert.True(pending.IsCommitting);
            Assert.Equal(sealedReview.Version.Revision, pending.Revision);
            Assert.IsType<Outcome<LibraryBatchStageReceipt>.Conflict>(await batches.StageAsync(track, Context(), token));
            revertBlocked = Assert.IsType<Outcome<LibraryBatchMode>.Conflict>(await dispatcher.SendAsync(
                new RevertLibraryBatch(sealedReview.Version), Context(), token)).Code;
            otherCommit = Assert.IsType<Outcome<LibraryBatchCommitReceipt>.Conflict>(await dispatcher.SendAsync(commit, Context(), token)).Code;
            receipt = Get(await dispatcher.SendAsync(new ResumeLibraryBatchCommit(), Context(), token));
            completed = Get(await dispatcher.QueryAsync(new GetLibraryBatchMode(), origin, token));
        }

        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false))
        {
            var (_, dispatcher) = Build(store);
            Assert.Equal(receipt, Get(await dispatcher.SendAsync(commit, commitContext, token)));
        }

        using (var database = new LiteDatabase(path))
        {
            Assert.Empty(database.GetCollection<BsonDocument>("library_batch_entries").FindAll());
            var receipts = database.GetCollection<BsonDocument>("library_batch_receipts").FindAll().ToArray();
            Assert.Equal(7, receipts.Length);
            Assert.DoesNotContain(receipts, row => row["Payload"].AsString.Contains("Downtempo", StringComparison.Ordinal));
            Assert.DoesNotContain(receipts, row => row["Payload"].AsString.Contains("Harbor.flac", StringComparison.Ordinal));
        }

        var reviewedPayload = JsonSerializer.Serialize(sealedReview);
        LibraryBatchLifecycleAssertions.Verify(new(initial.IsEnabled, afterRestart, [.. listed.Entries.Select(Describe)], replayed, dirtyDisable,
            stale, beforeTransfer, revertTransfer, reverted.IsEnabled, reverted.ChangeCount, closedStage, lostReply, revertBlocked, otherCommit,
            [.. publisher.Calls.Select(call => call.Operation)], commitContext.OperationId,
            publisher.Calls.All(call => call.Scope == scope && call.Review == reviewedPayload), receipt.Version.BatchId, sealedReview.Version.BatchId,
            receipt.CatalogRevision, completed.IsEnabled, completed.ChangeCount));

        CommandContext Context() => new(origin, Guid.NewGuid());

        (LibraryBatchHandlers Handler, RequestDispatcher Dispatcher) Build(LiteDbDesktopPlaylistStore store)
        {
            var handlers = new LibraryBatchHandlers(scope, store, publisher);
            return (handlers, handlers.Register(new RequestDispatcher.Builder()).Build());
        }
    }

    private static string Describe(LibraryBatchEntry entry) => entry.Change switch
    {
        LibraryBatchChange.TrackTags => "Track tags",
        LibraryBatchChange.Playlist { QueueSource: null } => "Playlist save",
        LibraryBatchChange.Playlist => "Queue auto-save",
        LibraryBatchChange.Import => "Import",
        LibraryBatchChange.Organize => "Organize",
        LibraryBatchChange.Remove { Plan.Mode: RemovalMode.CatalogAndAudio } => "Remove audio",
        _ => "Unrecognized change"
    };

    private static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;

    /// <summary>Returns independently supplied acknowledgments while recording the operation sent on every attempt.</summary>
    /// <remarks>2026-11-26: Simulate malformed and corrected replies for FR-LIBRARY-002#ac-batch-commit.</remarks>
    private sealed class ReplaceablePublisher : IApprovedLibraryBatchPublisher
    {
        public LibraryBatchCommitReceipt? Receipt { get; set; }
        public List<Guid> Operations { get; } = [];

        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved,
            Guid operationId, CancellationToken token)
        {
            Operations.Add(operationId);
            return Task.FromResult<Outcome<LibraryBatchCommitReceipt>>(new Outcome<LibraryBatchCommitReceipt>.Success(Receipt!));
        }
    }

    /// <summary>Models one acknowledged host publication whose first response never reached the desktop.</summary>
    private sealed class RecordingPublisher : IApprovedLibraryBatchPublisher
    {
        private LibraryBatchCommitReceipt? receipt;
        public List<(LibraryBatchScope Scope, Guid Operation, string Review)> Calls { get; } = [];

        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId, CancellationToken token)
        {
            Calls.Add((scope, operationId, JsonSerializer.Serialize(approved)));
            if (receipt is not null)
                return Task.FromResult<Outcome<LibraryBatchCommitReceipt>>(new Outcome<LibraryBatchCommitReceipt>.Success(receipt));
            receipt = new(scope.LibraryId, approved.Version, operationId, 43);
            return Task.FromResult<Outcome<LibraryBatchCommitReceipt>>(new Outcome<LibraryBatchCommitReceipt>.DependencyFailure("library-batch-host-unreachable", true));
        }
    }
}
