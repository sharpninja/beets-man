using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Binds the atomic and retry assertions to real disposable desktop database files.</summary>
/// <remarks>2026-11-24: Verify cross-collection rollback, durable reads and explicit schema creation for TEST-RECORDS-002.</remarks>
public sealed class LiteDbPlaylistStoreTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-playlist-");
    private readonly CancellationToken stop = TestContext.Current.CancellationToken;

    [Fact]
    public async Task RejectedCallbackRollsBackBothCollectionsAndAcceptedReceiptSurvivesReopen()
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
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        Assert.Equal(originalStoreId, reopened.StoreId);
        var persisted = await ReadState(reopened, draft.DraftId, operation.Key);
        TransactionAssertions.VerifyCommitted(persisted.Draft?.Name, persisted.Operation?.Status.ToString().ToLowerInvariant());
        Assert.Equal(receipt, persisted.Operation?.Receipt);
        Assert.Equal(draft.Entries.ToArray(), persisted.Draft?.Entries.ToArray());
        Assert.Equal(draft.Entries.ToArray(), persisted.Operation?.Entries.ToArray());
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
