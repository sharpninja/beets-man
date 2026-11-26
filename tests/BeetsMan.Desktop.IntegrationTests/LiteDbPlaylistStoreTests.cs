using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Binds the atomic and retry assertions to real disposable desktop database files.</summary>
/// <remarks>2026-11-24: Verify cross-collection rollback, durable reads and explicit schema creation for TEST-RECORDS-002.</remarks>
public sealed class RECORDSAcceptanceTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-playlist-");
    private readonly CancellationToken stop = TestContext.Current.CancellationToken;

    /// <summary>Rejects stale and duplicate writes, rolls back cross-collection effects, and reopens the original receipt.</summary>
    /// <remarks>2026-11-26: Bind repository atomicity and key rules for FR-RECORDS-001#ac-replicate and TR-RECORDS-ARCH-001#ac-litedb-repositories.</remarks>
    [Fact]
    public async Task RepositoryWrites_MatchesContract()
    {
        var path = Path.Combine(directory.FullName, "desktop.db");
        var draft = Draft();
        var receipt = new PlaylistSavedReceipt(draft.DraftId, Guid.NewGuid(), 3);
        var operation = new PendingPlaylistSave(new(Guid.NewGuid(), Guid.NewGuid()), draft.DraftId, draft.Revision, null,
            draft.Name, draft.Entries, PendingPlaylistSaveStatus.Accepted, receipt);
        Guid originalStoreId;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            originalStoreId = store.StoreId;
            var rejected = await store.ExecuteAsync<string>(repositories =>
            {
                repositories.Drafts.Add(draft);
                repositories.Operations.Add(operation);
                return new Outcome<string>.Conflict("revision-changed", 1);
            }, stop);
            Assert.IsType<Outcome<string>.Conflict>(rejected);
            var lost = await ReadState(store, draft.DraftId, operation.Key);
            TransactionAssertions.VerifyRejected(lost.Draft?.Name, lost.Operation?.Status.ToString().ToLowerInvariant());

            var accepted = await store.ExecuteAsync<string>(repositories =>
            {
                repositories.Drafts.Add(draft);
                repositories.Operations.Add(operation);
                return new Outcome<string>.Success("accepted");
            }, stop);
            Assert.IsType<Outcome<string>.Success>(accepted);

            var stale = await store.ExecuteAsync<bool>(repositories => new Outcome<bool>.Success(
                repositories.Drafts.TryReplace(draft with { Name = "wrong revision" }, draft.Revision - 1)), stop);
            Assert.False(Assert.IsType<Outcome<bool>.Success>(stale).Value);

            var accidental = Draft();
            var duplicate = await store.ExecuteAsync<bool>(repositories =>
            {
                repositories.Drafts.Add(accidental);
                repositories.Operations.Add(operation with { Name = "different intent" });
                return new Outcome<bool>.Success(true);
            }, stop);
            Assert.Equal("desktop-store-error", Assert.IsType<Outcome<bool>.DependencyFailure>(duplicate).Code);
            var rejectedDuplicate = await ReadState(store, accidental.DraftId, operation.Key);
            Assert.Null(rejectedDuplicate.Draft);
            Assert.Equal(operation.Name, rejectedDuplicate.Operation?.Name);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        Assert.Equal(originalStoreId, reopened.StoreId);
        var persisted = await ReadState(reopened, draft.DraftId, operation.Key);
        TransactionAssertions.VerifyCommitted(persisted.Draft?.Name, persisted.Operation?.Status.ToString().ToLowerInvariant());
        Assert.Equal(receipt, persisted.Operation?.Receipt);
        Assert.Equal(draft.Entries.ToArray(), persisted.Draft?.Entries.ToArray());
        Assert.Equal(draft.Entries.ToArray(), persisted.Operation?.Entries.ToArray());

        reopened.Dispose();
        VerifyDraftAndOperationDocuments(path, draft, operation, receipt);
    }

    /// <summary>Reads the persisted document keys and verifies the pending operation's relation to the draft.</summary>
    /// <remarks>2026-11-26: Verify the desktop draft and operation shapes for TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
    private static void VerifyDraftAndOperationDocuments(string path, PlaylistDraft draft, PendingPlaylistSave operation, PlaylistSavedReceipt receipt)
    {
        using var database = new LiteDatabase(path);
        var mappedDraft = database.GetCollection<BsonDocument>("playlist_drafts").FindById(draft.DraftId);
        Assert.Equal(draft.Name, mappedDraft["ProposedName"].AsString);
        Assert.Equal(draft.Revision, mappedDraft["DraftRevision"].AsInt64);
        Assert.Equal(draft.Entries.Select(entry => entry.TrackId), mappedDraft["Entries"].AsArray.Select(entry => entry.AsDocument["TrackId"].AsGuid));
        var key = $"{operation.Key.OriginInstanceId:N}/{operation.Key.OperationId:N}";
        var mappedOperation = database.GetCollection<BsonDocument>("pending_operations").FindById(key);
        Assert.Equal(operation.Key.OriginInstanceId, mappedOperation["OriginInstanceId"].AsGuid);
        Assert.Equal(operation.Key.OperationId, mappedOperation["OperationId"].AsGuid);
        Assert.Equal(draft.DraftId, mappedOperation["DraftId"].AsGuid);
        Assert.Equal("SavePlaylist", mappedOperation["CommandType"].AsString);
        Assert.Equal("Accepted", mappedOperation["State"].AsString);
        Assert.Equal(receipt.PlaylistId, mappedOperation["Payload"]["CommittedPlaylistId"].AsGuid);
        Assert.Equal(receipt.Revision, mappedOperation["Payload"]["CommittedRevision"].AsInt64);
    }

    [Fact]
    public async Task CancellationBeforeCommitAndStaleDraftEditsLeaveTheStoreConsistent()
    {
        var draft = Draft();
        using var store = LiteDbDesktopPlaylistStore.Open(Path.Combine(directory.FullName, "cancel.db"), allowCreate: true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var result = await store.ExecuteAsync<string>(repositories =>
        {
            repositories.Drafts.Add(draft);
            cancel.Cancel();
            return new Outcome<string>.Success("not committed");
        }, cancel.Token);
        Assert.IsType<Outcome<string>.Cancelled>(result);
        Assert.Null(Assert.IsType<Outcome<PlaylistDraft?>.Success>(await store.ReadDraftAsync(draft.DraftId, stop)).Value);

        IPlaylistDraftRepository? expired = null;
        var commit = await store.ExecuteAsync<bool>(repositories =>
        {
            expired = repositories.Drafts;
            repositories.Drafts.Add(draft);
            return new Outcome<bool>.Success(repositories.Drafts.TryReplace(draft with { Name = "old" }, draft.Revision - 1));
        }, stop);
        Assert.False(Assert.IsType<Outcome<bool>.Success>(commit).Value);
        Assert.Throws<InvalidOperationException>(() => expired!.Find(draft.DraftId));
        var preserved = await store.ReadDraftAsync(draft.DraftId, stop);
        Assert.Equal(draft.Name, Assert.IsType<Outcome<PlaylistDraft?>.Success>(preserved).Value?.Name);
    }

    /// <summary>Checks the documented schema document identity before opening a desktop repository.</summary>
    /// <remarks>2026-11-26: Exercise the StoreSchema collection mapping for TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
    [Fact]
    public void OpenRequiresCreationApprovalAndRejectsAnotherDatabaseRole()
    {
        var path = Path.Combine(directory.FullName, "different.db");
        Assert.Throws<FileNotFoundException>(() => LiteDbDesktopPlaylistStore.Open(path, allowCreate: false));
        Assert.False(File.Exists(path));
        using (var other = new LiteDatabase(path))
        {
            other.GetCollection<BsonDocument>("store_schema").Insert(new BsonDocument
            {
                ["_id"] = "store", ["StoreRole"] = "Host", ["SchemaVersion"] = 1, ["StoreId"] = Guid.NewGuid()
            });
        }
        Assert.Throws<InvalidDataException>(() => LiteDbDesktopPlaylistStore.Open(path, allowCreate: true));
    }

    public void Dispose() => directory.Delete(recursive: true);

    private static PlaylistDraft Draft() => new(Guid.NewGuid(), 2, "staged-name",
        [new(Guid.NewGuid(), Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid())], null);

    private async Task<(PlaylistDraft? Draft, PendingPlaylistSave? Operation)> ReadState(LiteDbDesktopPlaylistStore store, Guid id, OperationKey key)
    {
        var result = await store.ExecuteAsync<(PlaylistDraft?, PendingPlaylistSave?)>(repositories =>
            new Outcome<(PlaylistDraft?, PendingPlaylistSave?)>.Success((repositories.Drafts.Find(id), repositories.Operations.Find(key))), stop);
        return Assert.IsType<Outcome<(PlaylistDraft?, PendingPlaylistSave?)>.Success>(result).Value;
    }
}
