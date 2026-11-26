using System.Globalization;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Xunit;
using static BeetsMan.Host.Tests.HostPlaylistAssertions;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Verifies actual host receipt, feed, and delivery commits against a disposable LiteDB file.</summary>
/// <remarks>2026-11-24: Bind TEST-RECORDS-001/002 for authoritative edits, authorization, replay, and rollback.</remarks>
public sealed class HostPlaylistIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-host-playlist-{Guid.NewGuid():N}");
    private readonly Guid library = Guid.NewGuid();
    private readonly Guid origin = Guid.NewGuid();
    private readonly Guid draft = Guid.NewGuid();
    private readonly Guid queueSession = Guid.NewGuid();
    private readonly Guid repeatedTrack = Guid.NewGuid();
    private string PathToDatabase => Path.Combine(root, "host.db");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private AuthenticatedHostInstance Caller => new(library, origin);

    /// <summary>Checks one publication transaction, including the document keys consumed across its collections.</summary>
    /// <remarks>2026-11-26: Verify host playlist, receipt, revision and delivery mappings for TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
    [Fact]
    public async Task QueueSaveCommitsAllCollectionsAndReturnsTheSameReceiptAfterReopen()
    {
        Seed();
        PlaylistSaveRequest change;
        PlaylistSaveResponse accepted;
        using (var store = LiteDbHostPlaylistStore.Open(PathToDatabase, false))
        {
            var writer = new HostPlaylistWriter(store);
            var created = Value(await writer.SaveAsync(library, Caller, Create(), Token));
            Assert.Equal(1, created.PlaylistRevision);
            change = Update(created);
            accepted = Value(await writer.SaveAsync(library, Caller, change, Token));
            Assert.Equal(queueSession, accepted.Source.SourceId);
        }

        using var reopened = LiteDbHostPlaylistStore.Open(PathToDatabase, false);
        var retry = new HostPlaylistWriter(reopened);
        var replay = Value(await retry.SaveAsync(library, Caller, change, Token));
        var projection = Value(await reopened.ReadPublicationAsync(library, accepted.FeedRevision, Token));
        var playlist = Assert.IsType<SavedHostPlaylist>(Value(await reopened.ReadPlaylistAsync(library, accepted.PlaylistId, Token)));
        Verify(Observe(accepted), playlist.Entries.Select(entry => entry.EntryId).ToArray(), projection.PendingRecipients, Observe(replay),
            projection.CurrentRevision);
        Assert.Equal(PlaylistOrder, projection.Change!.Entries.Select(entry => entry.EntryId));
        Assert.Equal(new HostOperationKey(origin, change.OperationId), projection.Origin);
        Assert.Equal(repeatedTrack, playlist.Entries[0].TrackId);
        Assert.Equal(repeatedTrack, playlist.Entries[1].TrackId);

        var differentIntent = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Rejected>(
            await retry.SaveAsync(library, Caller, change with { Name = "Different intent" }, Token));
        Assert.True(differentIntent.Conflict);
        Assert.Equal("operation-intent-mismatch", differentIntent.Code);
        var stale = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Rejected>(
            await retry.SaveAsync(library, Caller, change with { OperationId = Guid.NewGuid() }, Token));
        Assert.Equal(2, stale.ActualRevision);
        Assert.Equal("stale-saved-playlist", stale.Code);
        Assert.Equal(accepted.FeedRevision, Value(await reopened.ReadPublicationAsync(library, 3, Token)).CurrentRevision);

        reopened.Dispose();
        VerifyPublicationDocuments(accepted, change);
    }

    /// <summary>Reads the durable document layout after the production store closes.</summary>
    /// <remarks>2026-11-26: Verify composite identities, typed fields and references for TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
    private void VerifyPublicationDocuments(PlaylistSaveResponse accepted, PlaylistSaveRequest change)
    {
        using var persisted = new LiteDatabase(PathToDatabase);
        var operationKey = $"{origin:N}/{change.OperationId:N}";
        var revisionKey = $"{library:N}/{accepted.FeedRevision.ToString(CultureInfo.InvariantCulture)}";
        var schema = persisted.GetCollection<BsonDocument>("store_schema").FindById("store");
        Assert.Equal("Host", schema["StoreRole"].AsString);
        Assert.NotEqual(Guid.Empty, schema["StoreId"].AsGuid);
        Assert.Equal(1, schema["SchemaVersion"].AsInt32);

        var body = persisted.GetCollection<BsonDocument>("saved_playlists").FindById(accepted.PlaylistId);
        Assert.Equal(library, body["LibraryId"].AsGuid);
        Assert.Equal(accepted.PlaylistRevision, body["Revision"].AsInt64);
        Assert.Equal(change.Name, body["Name"].AsString);
        Assert.Equal(PlaylistOrder, body["Entries"].AsArray.Select(item => item.AsDocument["EntryId"].AsGuid));
        Assert.Equal(repeatedTrack, body["Entries"].AsArray[0].AsDocument["TrackId"].AsGuid);

        var receipt = persisted.GetCollection<BsonDocument>("operation_receipts").FindById(operationKey);
        Assert.Equal(origin, receipt["OriginInstanceId"].AsGuid);
        Assert.Equal(change.OperationId, receipt["OperationId"].AsGuid);
        Assert.NotEmpty(receipt["IntentFingerprint"].AsString);
        Assert.Equal(accepted.PlaylistId, receipt["Receipt"]["PlaylistId"].AsGuid);
        Assert.Equal(accepted.FeedRevision, receipt["Receipt"]["FeedRevision"].AsInt64);
        Assert.Equal(accepted.FeedRevision, persisted.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64);
        var revision = persisted.GetCollection<BsonDocument>("revision_changes").FindById(revisionKey);
        Assert.Equal(change.OperationId, revision["OperationId"].AsGuid);
        Assert.Equal(origin, revision["OriginInstanceId"].AsGuid);
        Assert.Equal(accepted.PlaylistId, Assert.Single(revision["Changes"].AsArray).AsDocument["Payload"]["_id"].AsGuid);

        var deliveries = persisted.GetCollection<BsonDocument>("notification_deliveries");
        foreach (var peer in new[] { RecipientB, RecipientC })
        {
            var delivery = deliveries.FindById($"{revisionKey}/{peer:N}");
            Assert.Equal(peer, delivery["RecipientInstanceId"].AsGuid);
            Assert.Equal(accepted.FeedRevision, delivery["Revision"].AsInt64);
            Assert.Equal(library, delivery["LibraryId"].AsGuid);
            Assert.Equal("Pending", delivery["Status"].AsString);
        }
    }

    [Fact]
    public async Task RevokedOrWrongLibraryOriginCannotWriteOrReadAReceiptByReplaying()
    {
        Seed();
        var request = Create();
        using (var store = LiteDbHostPlaylistStore.Open(PathToDatabase, false))
        {
            var writer = new HostPlaylistWriter(store);
            Assert.IsType<HostOutcome<PlaylistSaveResponse>.Forbidden>(await writer.SaveAsync(library, Caller,
                request with { OriginInstanceId = RecipientB }, Token));
            Assert.IsType<HostOutcome<PlaylistSaveResponse>.Forbidden>(await writer.SaveAsync(Guid.NewGuid(), Caller, request, Token));
            Assert.Equal(1, Value(await writer.SaveAsync(library, Caller, request, Token)).FeedRevision);
        }

        using (var fixture = new LiteDatabase(PathToDatabase))
        {
            var registry = fixture.GetCollection<BsonDocument>("registered_instances");
            var record = registry.FindById(origin);
            record["Status"] = "Revoked";
            registry.Update(record);
        }
        using var reopened = LiteDbHostPlaylistStore.Open(PathToDatabase, false);
        var revoked = new HostPlaylistWriter(reopened);
        Assert.IsType<HostOutcome<PlaylistSaveResponse>.Forbidden>(await revoked.SaveAsync(library, Caller, request, Token));
        Assert.IsType<HostOutcome<PlaylistSaveResponse>.Forbidden>(await revoked.SaveAsync(library, Caller,
            request with { OperationId = Guid.NewGuid() }, Token));
        Assert.Equal(1, Value(await reopened.ReadPublicationAsync(library, 2, Token)).CurrentRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollingBackAfterEveryCollectionWriteRemovesTheEntirePublication(bool cancel)
    {
        Seed();
        using var store = LiteDbHostPlaylistStore.Open(PathToDatabase, false);
        var id = Guid.NewGuid();
        var operation = new HostOperationKey(origin, Guid.NewGuid());
        var item = new SavedHostPlaylist(library, id, 1, "Rolled back", [new(EntryFirst, repeatedTrack)]);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var write = await store.ExecuteAsync<bool>(repositories =>
        {
            repositories.Playlists.Add(item);
            var feed = repositories.Publications.Append(item, operation);
            repositories.Operations.Add(operation,
                new("fixture-fingerprint", new(operation.OperationId, new(PlaylistSaveSourceKind.Draft, draft), id, 1, feed)));
            if (!cancel)
                return new HostOutcome<bool>.Rejected("injected-rejection");
            abort.Cancel();
            return new HostOutcome<bool>.Success(true);
        }, abort.Token);
        if (cancel)
            Assert.IsType<HostOutcome<bool>.Cancelled>(write);
        else
            Assert.IsType<HostOutcome<bool>.Rejected>(write);

        var change = Value(await store.ReadPublicationAsync(library, 1, Token));
        VerifyRolledBack(0, change.CurrentRevision, Value(await store.ReadPlaylistAsync(library, id, Token)) is not null,
            change.Change is not null, !change.PendingRecipients.IsEmpty);
        Assert.False(Value(await store.ExecuteAsync<bool>(
            repositories => new HostOutcome<bool>.Success(repositories.Operations.Find(operation) is not null), Token)));
    }

    [Fact]
    public async Task ConcurrentSavesWithTheSameExpectedRevisionCommitOnlyOnce()
    {
        Seed();
        using var store = LiteDbHostPlaylistStore.Open(PathToDatabase, false);
        var writer = new HostPlaylistWriter(store);
        var created = Value(await writer.SaveAsync(library, Caller, Create(), Token));
        var first = Update(created);
        var results = await Task.WhenAll(
            writer.SaveAsync(library, Caller, first, Token),
            writer.SaveAsync(library, Caller, first with { OperationId = Guid.NewGuid(), Name = "Second candidate" }, Token));
        Assert.Single(results.OfType<HostOutcome<PlaylistSaveResponse>.Success>());
        Assert.Equal("stale-saved-playlist", Assert.Single(results.OfType<HostOutcome<PlaylistSaveResponse>.Rejected>()).Code);
        Assert.Equal(2, Value(await store.ReadPublicationAsync(library, 2, Token)).CurrentRevision);
    }

    private PlaylistSaveRequest Create() => new(origin, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, draft), null, null,
        "Saved playlist", [new(EntryFirst, repeatedTrack), new(EntrySecond, repeatedTrack)]);

    private PlaylistSaveRequest Update(PlaylistSaveResponse created) => new(origin, Guid.NewGuid(), new(PlaylistSaveSourceKind.Queue, queueSession),
        created.PlaylistId, created.PlaylistRevision, "Saved playlist",
        [new(EntrySecond, repeatedTrack), new(EntryFirst, repeatedTrack), new(EntryThird, Guid.NewGuid())]);

    private void Seed()
    {
        using (LiteDbHostPlaylistStore.Open(PathToDatabase, true)) { }
        using var fixture = new LiteDatabase(PathToDatabase);
        var registry = fixture.GetCollection<BsonDocument>("registered_instances");
        registry.Insert(Instance(library, origin, "Active"));
        registry.Insert(Instance(library, RecipientB, "Active"));
        registry.Insert(Instance(library, RecipientC, "Active"));
        registry.Insert(Instance(library, Guid.NewGuid(), "Revoked"));
        registry.Insert(Instance(Guid.NewGuid(), Guid.NewGuid(), "Active"));
    }

    private static BsonDocument Instance(Guid library, Guid id, string status) => new()
    {
        ["_id"] = id, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = status
    };

    private static T Value<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;
    private static ObservedPublication Observe(PlaylistSaveResponse receipt) => new(receipt.PlaylistId, receipt.PlaylistRevision, receipt.FeedRevision);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
