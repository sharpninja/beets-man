using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies closed-loop playlist batch publication and scope-safe mixed admission in the real stores.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 for FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-commit.</remarks>
    [Fact]
    public async Task ApprovedBatchReplaysOnePlaylistRevisionAndJournalsMixedEffectsWhole()
    {
        const string peerToken = "batch-peer-fixture";
        var hostDb = Path.Combine(root, "batch-host.db");
        var localDb = Path.Combine(root, "batch-local.db");
        var peerDb = Path.Combine(root, "batch-peer.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var peer = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var scope = new LibraryBatchScope(library, instance);
        var read = new RequestContext(Guid.NewGuid(), instance);
        var draft = new PlaylistDraft(Guid.NewGuid(), 2, "Offline draft", [], null);
        var finalEntries = new PlaylistEntry[] { new(Guid.NewGuid(), second), new(Guid.NewGuid(), first), new(Guid.NewGuid(), second) };
        var parent = Command();
        var mixedParent = Command();
        SeedHost(hostDb, revokedInstance: revoked);
        using (var fixture = new LiteDatabase(hostDb))
        {
            fixture.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            fixture.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 20L });
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(first, "First", 31));
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(second, "Second", 32));
        }
        using (LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: true)) { }
        using (var fixture = new LiteDatabase(peerDb))
            fixture.GetCollection<BsonDocument>("sync_checkpoints").Insert(new BsonDocument
                { ["_id"] = $"{library:N}/catalog", ["LibraryId"] = library, ["Feed"] = "catalog", ["AppliedRevision"] = 20L });

        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        { [secret] = new(library, instance), [peerToken] = new(library, peer), [RevokedCredential] = new(library, revoked) });
        bool beforeApproval;
        bool retained;
        ApproveLibraryBatchRequest original;
        using (var local = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: true))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var direct = Client(address, secret);
            using var transport = new BatchApprovalTransport(CreateClientHandler(certificate), loseFirst: true);
            using var http = new HttpClient(transport);
            var batches = Batch(local, http, address);
            Success(await local.ExecuteAsync<bool>(repos => { repos.Drafts.Add(draft); return new Outcome<bool>.Success(true); }, Token));
            Success(await batches.HandleAsync(new SetLibraryBatchMode(true, 0), Command(), Token));
            Success(await batches.StageAsync(new LibraryBatchChange.Playlist(draft.DraftId, 1, null, "Earlier ordering", [finalEntries[1]]), Command(), Token));
            Success(await batches.StageAsync(new LibraryBatchChange.Playlist(draft.DraftId, 2, null, "Final ordering", [.. finalEntries]), Command(), Token));
            var review = Success(await batches.HandleAsync(new GetLibraryBatchReview(), read, Token));
            var empty = await direct.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 20, null, 10), Token);
            using var missing = await direct.GetAsync(ApprovedLibraryBatchRoutes.StatusPath(library, parent.OperationId), Token);
            beforeApproval = transport.Attempts.Count == 0 && empty!.Revisions.IsEmpty && missing.StatusCode == HttpStatusCode.NotFound;
            var failed = await batches.HandleAsync(new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value)), parent, Token);
            retained = failed is Outcome<LibraryBatchCommitReceipt>.DependencyFailure { Code: "library-batch-submit-host-unreachable", Retryable: true } &&
                Success(await batches.HandleAsync(new GetLibraryBatchMode(), read, Token)).IsCommitting;
            original = Assert.Single(transport.Attempts);
            using var inaccessible = new HttpClient(CreateClientHandler(certificate));
            var denied = new LibraryBatchHandlers(scope, local, new HttpApprovedLibraryBatchPublisher(inaccessible, address, library,
                new TokenProvider(library, instance, "expired-fixture-credentials")));
            var uncertain = await denied.HandleAsync(new ResumeLibraryBatchCommit(), Command(), Token);
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(uncertain);
            Assert.Equal("library-batch-outcome-unavailable", ((Outcome<LibraryBatchCommitReceipt>.DependencyFailure)uncertain).Code);
            Assert.True(Success(await batches.HandleAsync(new GetLibraryBatchMode(), read, Token)).IsCommitting);
        }

        LibraryBatchCommitReceipt published;
        PlaylistVersion peerVersion;
        bool sameOperation;
        bool staleAtomic;
        bool mixedPrivate;
        bool mixedSealed;
        bool changedRejected;
        bool foreignRead;
        bool replayChild;
        bool reserved;
        bool revokedRead;
        string[] names;
        string[] order;
        using (var local = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: false))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var direct = Client(address, secret);
            using var peerHttp = Client(address, peerToken);
            using var revokedHttp = Client(address, RevokedCredential);
            using var transport = new BatchApprovalTransport(CreateClientHandler(certificate), loseFirst: false);
            using var batchHttp = new HttpClient(transport);
            var batches = Batch(local, batchHttp, address);
            published = Success(await batches.HandleAsync(new ResumeLibraryBatchCommit(), Command(), Token));
            var replay = Assert.Single(transport.Attempts);
            sameOperation = original.OperationId == replay.OperationId && original.ReviewId == replay.ReviewId &&
                original.Entries.Select(entry => entry.OperationId).SequenceEqual(replay.Entries.Select(entry => entry.OperationId));
            var saved = Assert.IsType<PlaylistDraft>(Success(await local.ReadDraftAsync(draft.DraftId, Token)));
            peerVersion = Assert.IsType<PlaylistVersion>(saved.BasePlaylist);
            Assert.Equal("Final ordering", saved.Name);
            Assert.Equal(2, published.Playlists.Length);
            Assert.All(published.Playlists, item => Assert.Equal(peerVersion, item.Version));
            using (var remote = LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: false))
            {
                var remoteContext = new RequestContext(Guid.NewGuid(), peer);
                var replica = new CatalogReplicaHandlers(library, peer, new HttpCatalogReplicaHost(peerHttp, address, library, peer,
                    new TokenProvider(library, peer, peerToken)), remote);
                var replicated = Success(await replica.HandleAsync(new ResumeReplication(library, peer, ReplicaRecoveryMode.Incremental, 20),
                    new(remoteContext, Guid.NewGuid()), Token));
                Assert.Equal(21, replicated.Cursor);
                var reader = new LiteDbCatalogReader(remote, library, music, () => false);
                var page = Success(await reader.HandleAsync(new BrowseCatalog(CatalogCategory.Playlists, new(), new(CatalogSortKey.Title)), remoteContext, Token));
                names = [.. page.Rows.Select(row => row.Title)];
                var playlist = Success(await reader.ReadAsync(peerVersion.PlaylistId, Token));
                Assert.Equal(peerVersion, playlist.Version);
                Assert.Equal(finalEntries.Select(entry => entry.EntryId), playlist.Entries.Select(entry => entry.EntryId));
                order = [.. playlist.Entries.Select(entry => entry.TrackId == first ? "First" : entry.TrackId == second ? "Second" : "Unrelated")];
            }
            var stale = new ApproveLibraryBatchRequest(instance, Guid.NewGuid(), Guid.NewGuid(), 4, Guid.NewGuid(),
                [new(Guid.NewGuid(), 1, Playlist("Would create", null, null)), new(Guid.NewGuid(), 2, Playlist("Old base", peerVersion.PlaylistId, 1))]);
            using var staleResponse = await direct.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), stale, Token);
            using var staleLookup = await direct.GetAsync(ApprovedLibraryBatchRoutes.StatusPath(library, stale.OperationId), Token);
            staleAtomic = staleResponse.StatusCode == HttpStatusCode.Conflict && staleLookup.StatusCode == HttpStatusCode.NotFound;

            using var preview = await direct.PostAsJsonAsync(LibraryMaintenanceRoutes.PreviewPath(library),
                new LibraryMaintenancePreviewRequest(LibraryMaintenanceKind.RemoveCatalog, 21, [second]), Token);
            preview.EnsureSuccessStatusCode();
            var reviewed = (await preview.Content.ReadFromJsonAsync<LibraryMaintenancePlan>(Token))!;
            var removal = new RemovalPlan(reviewed.PlanId, library, reviewed.CatalogRevision, RemovalMode.CatalogOnly, reviewed.TrackIds, []);
            Success(await batches.StageAsync(new LibraryBatchChange.TrackTags(first, 20, ImmutableSortedDictionary<string, string>.Empty.Add("title", "First"),
                ImmutableSortedDictionary<string, string?>.Empty.Add("title", "Retitle")), Command(), Token));
            Success(await batches.StageAsync(new LibraryBatchChange.Import([new(Guid.NewGuid(), "New.flac", 12, new string('A', 64))]), Command(), Token));
            Success(await batches.StageAsync(new LibraryBatchChange.Remove(removal, new(removal.PlanId, removal.Mode)), Command(), Token));
            var child = Command();
            Success(await batches.StageAsync(new LibraryBatchChange.Playlist(Guid.NewGuid(), 1, null, "Mixed edit", [finalEntries[0]]), child, Token));
            var mixedReview = Success(await batches.HandleAsync(new GetLibraryBatchReview(), read, Token));
            var pending = await batches.HandleAsync(new CommitLibraryBatch(new(mixedReview.Version, mixedReview.ReviewId!.Value)), mixedParent, Token);
            var recorded = transport.Attempts[1];
            var status = await direct.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, mixedParent.OperationId), Token);
            var unadvanced = await direct.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 21, null, 10), Token);
            mixedPrivate = status is { Phase: BatchPublicationPhase.Accepted, CatalogRevision: null } && unadvanced!.Revisions.IsEmpty;
            mixedSealed = pending is Outcome<LibraryBatchCommitReceipt>.DependencyFailure { Code: "private-import-stage-unavailable", Retryable: true } &&
                Success(await batches.HandleAsync(new GetLibraryBatchMode(), read, Token)).IsCommitting;
            var reordered = recorded with { Entries = [.. recorded.Entries.Reverse().Select((entry, index) => entry with { Position = index + 1 })] };
            using var changed = await direct.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), reordered, Token);
            changedRejected = changed.StatusCode == HttpStatusCode.Conflict;
            using var peerLookup = await peerHttp.GetAsync(ApprovedLibraryBatchRoutes.StatusPath(library, mixedParent.OperationId), Token);
            foreignRead = peerLookup.StatusCode == HttpStatusCode.NotFound;
            using var revokedLookup = await revokedHttp.GetAsync(ApprovedLibraryBatchRoutes.StatusPath(library, mixedParent.OperationId), Token);
            revokedRead = revokedLookup.StatusCode == HttpStatusCode.Forbidden;
            using var childReplay = await direct.PostAsJsonAsync(PlaylistPublicationRoute.Path(library), new PlaylistSaveRequest(instance, child.OperationId,
                new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Bypass", [new(Guid.NewGuid(), first)]), Token);
            replayChild = childReplay.StatusCode == HttpStatusCode.Conflict;
            using var directConfirm = await direct.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, reviewed.PlanId),
                new ConfirmLibraryMaintenanceRequest(instance, Guid.NewGuid(), LibraryMaintenanceKind.RemoveCatalog, 21), Token);
            reserved = directConfirm.StatusCode == HttpStatusCode.Conflict;
        }

        using var final = new LiteDatabase(hostDb);
        var journal = final.GetCollection<BsonDocument>("library_batch_publications").FindAll().Single(row => row["OperationId"].AsGuid == mixedParent.OperationId);
        var mixed = final.GetCollection<BsonDocument>("library_batch_publication_entries").Find(Query.EQ("PublicationKey", journal["_id"]))
            .OrderBy(row => row["Position"].AsInt32).Select(row => row["Kind"].AsString).ToArray();
        var revisions = final.GetCollection<BsonDocument>("revision_changes").FindAll().ToArray();
        var publication = revisions.Length == 1 && revisions[0]["OperationId"].AsGuid == parent.OperationId &&
            revisions[0]["Changes"].AsArray.Count == 1 && final.GetCollection<BsonDocument>("saved_playlists").Count() == 1;
        Assert.Equal(2, final.GetCollection<BsonDocument>("catalog_tracks").Count());
        Assert.Empty(final.GetCollection<BsonDocument>("library_jobs").FindAll());
        Assert.Equal(2, final.GetCollection<BsonDocument>("library_batch_publications").Count());
        ApprovedBatchPublicationAssertions.Verify(new(beforeApproval, retained, sameOperation, published.CatalogRevision, names, order, peerVersion.Revision,
            publication, staleAtomic && publication, mixed, mixedPrivate, mixedSealed, changedRejected, foreignRead, replayChild, reserved, revokedRead));

        BsonDocument Track(Guid id, string title, int nativeId) => new() { ["_id"] = id, ["LibraryId"] = library, ["Revision"] = 20L,
            ["BeetsTrackId"] = nativeId, ["Title"] = title, ["RelativePath"] = title + ".flac" };
        CommandContext Command() => new(read, Guid.NewGuid());
        LibraryBatchHandlers Batch(LiteDbDesktopPlaylistStore store, HttpClient http, Uri address) => new(scope, store,
            new HttpApprovedLibraryBatchPublisher(http, address, library, new TokenProvider(library, instance, secret)));
        ApprovedBatchChange.Playlist Playlist(string name, Guid? id, long? revision) => new(new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()),
            1, id, revision, name, [new(Guid.NewGuid(), first)]);
        WebApplication Host()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, Path.Combine(root, "private"), Path.Combine(root, "work"), []), verifier,
                credentials, credentials);
        }
        HttpClient Client(Uri address, string token)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        static T Success<T>(Outcome<T> value) => Assert.IsType<Outcome<T>.Success>(value).Value;
    }

    private sealed class BatchApprovalTransport(HttpMessageHandler inner, bool loseFirst) : DelegatingHandler(inner)
    {
        public List<ApproveLibraryBatchRequest> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Attempts.Add((await request.Content!.ReadFromJsonAsync<ApproveLibraryBatchRequest>(token))!);
            var response = await base.SendAsync(request, token);
            if (!loseFirst || Attempts.Count != 1 || !response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw new HttpRequestException("The committed batch reply was interrupted.");
        }
    }
}
