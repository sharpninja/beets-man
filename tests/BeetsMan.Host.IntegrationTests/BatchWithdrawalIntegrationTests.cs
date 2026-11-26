using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
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
    /// <summary>Reverts an admitted approval after losing the withdrawal reply and blocks a delayed approval.</summary>
    /// <remarks>2026-11-25: Bind TEST-LIBRARY-004 to the desktop CQRS, both real stores, and authenticated HTTPS.</remarks>
    /// <remarks>2026-11-26: Verify that withdrawing an uncommitted approval preserves revert behavior for FR-LIBRARY-002#ac-batch-revert.</remarks>
    [Fact]
    public async Task WithdrawalSurvivesLostReplyAndFencesDelayedApprovals()
    {
        var hostDb = Path.Combine(root, "withdraw-host.db");
        var desktopDb = Path.Combine(root, "withdraw-desktop.db");
        var stage = Path.Combine(root, "withdraw-local-copies");
        var music = Directory.CreateDirectory(Path.Combine(root, "withdraw-music")).FullName;
        var original = Path.Combine(root, "withdraw-source.flac");
        await File.WriteAllTextAsync(original, "Original selected audio", Token);
        SeedHost(hostDb);
        var foreign = Guid.NewGuid();
        const string foreignToken = "batch-withdrawal-foreign";
        using (var db = new LiteDatabase(hostDb))
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = foreign, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [foreignToken] = new(library, foreign) });
        var options = new LibraryHostOptions(library, hostDb, music, Path.Combine(root, "withdraw-host-copies"), Path.Combine(root, "withdraw-work"), []);
        var configured = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var tokens = new TokenProvider(library, instance, secret);
        var scope = new LibraryBatchScope(library, instance);
        var request = new RequestContext(Guid.NewGuid(), instance);
        using var certificate = CreateCertificate();
        var approve = Command();
        var revert = Command();
        RevertLibraryBatch revertCommand;
        string acceptedPhase;
        int retained;
        bool foreignDenied;
        bool mismatchedScopeDenied;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: true))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            using var client = Client(host, secret, loseWithdrawal: true);
            using var raw = Client(host, secret);
            using var outsider = Client(host, foreignToken);
            var batches = Batches(desktop, client);
            Get(await batches.HandleAsync(new SetLibraryBatchMode(true, 0), Command(), Token));
            var import = new ImportIntakeHandlers(library, new ApprovedImportPicker([original]), new HttpImportIntakeHost(client, client.BaseAddress!, library, tokens), batches);
            var picked = Get(await import.HandleAsync(new SelectImportSources(Guid.NewGuid()), Command(), Token));
            Assert.NotNull(Get(await import.HandleAsync(new UploadImportSources(library, picked.SelectionToken), Command(), Token)).LocalBatch);
            var reviewed = Get(await batches.HandleAsync(new GetLibraryBatchReview(), request, Token));
            var reviewId = reviewed.ReviewId!.Value;
            revertCommand = new(reviewed.Version);
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(await batches.HandleAsync(
                new CommitLibraryBatch(new(reviewed.Version, reviewId)), approve, Token));
            var ready = await Status(raw, approve.OperationId);
            Assert.Equal(BatchPublicationPhase.Accepted, ready.Phase);
            Assert.Single(ready.TransferredImports);
            var exact = new WithdrawApprovedLibraryBatchRequest(instance, reviewed.Version.BatchId, reviewed.Version.Revision, reviewId);
            using var denied = await Withdraw(outsider, approve.OperationId, exact);
            foreignDenied = denied.StatusCode == HttpStatusCode.Forbidden;
            using var mismatched = await Withdraw(raw, approve.OperationId, exact with { ReviewId = Guid.NewGuid() });
            mismatchedScopeDenied = mismatched.StatusCode == HttpStatusCode.Conflict && await Code(mismatched) == "operation-intent-mismatch";
            Assert.Equal(BatchPublicationPhase.Accepted, (await Status(raw, approve.OperationId)).Phase);
            Assert.IsType<Outcome<LibraryBatchMode>.DependencyFailure>(await batches.HandleAsync(revertCommand, revert, Token));
            var pending = Get(await batches.HandleAsync(new GetLibraryBatchMode(), request, Token));
            retained = pending.ChangeCount;
            Assert.True(pending.IsCommitting);
            Assert.Single(Directory.GetFiles(stage, "*.copy", SearchOption.AllDirectories));
            acceptedPhase = (await Status(raw, approve.OperationId)).Phase.ToString();
        }

        var unseen = new ApproveLibraryBatchRequest(instance, Guid.NewGuid(), Guid.NewGuid(), 7, Guid.NewGuid(), [PlaylistEntry("Delayed playlist")]);
        string unseenPhase;
        long feedRevision;
        string lateSubmit;
        string alreadyStarted;
        int afterRecovery;
        string replayedPhase;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: false))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            using var raw = Client(host, secret);
            var batches = Batches(desktop, raw);
            var reverted = Get(await batches.HandleAsync(revertCommand, revert, Token));
            afterRecovery = reverted.ChangeCount;
            Assert.True(reverted.IsEnabled);
            Assert.False(reverted.IsCommitting);
            Assert.NotEqual(revertCommand.Version.BatchId, reverted.BatchId);
            Assert.Empty(Directory.GetFiles(stage, "*.copy", SearchOption.AllDirectories));
            replayedPhase = (await Status(raw, approve.OperationId)).Phase.ToString();
            using var unseenWithdraw = await Withdraw(raw, unseen.OperationId, Intent(unseen));
            unseenWithdraw.EnsureSuccessStatusCode();
            unseenPhase = (await Status(raw, unseen.OperationId)).Phase.ToString();
            using var late = await raw.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), unseen, Token);
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            lateSubmit = await Code(late);
            using var changedOperation = await raw.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), unseen with { OperationId = Guid.NewGuid() }, Token);
            Assert.Equal(HttpStatusCode.Conflict, changedOperation.StatusCode);
            Assert.Equal("library-batch-already-admitted", await Code(changedOperation));
            var feed = Assert.IsType<CatalogFeedPage>(await raw.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 0, null, 10), Token));
            feedRevision = feed.UpperRevision;
            Assert.Empty(feed.Revisions);
            Assert.Empty(Directory.GetFileSystemEntries(music));

            var published = new ApproveLibraryBatchRequest(instance, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), [PlaylistEntry("Already published")]);
            using var accepted = await raw.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), published, Token);
            accepted.EnsureSuccessStatusCode();
            Assert.Equal(BatchPublicationPhase.Published, (await Status(raw, published.OperationId)).Phase);
            using var tooLate = await Withdraw(raw, published.OperationId, Intent(published));
            Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
            alreadyStarted = await Code(tooLate);
            Assert.Equal(BatchPublicationPhase.Published, (await Status(raw, published.OperationId)).Phase);
        }
        BatchWithdrawalAssertions.Verify(new(acceptedPhase, unseenPhase, lateSubmit, alreadyStarted, feedRevision, retained, afterRecovery,
            await File.ReadAllTextAsync(original, Token) == "Original selected audio", foreignDenied, mismatchedScopeDenied, replayedPhase));

        CommandContext Command() => new(request, Guid.NewGuid());
        static WithdrawApprovedLibraryBatchRequest Intent(ApproveLibraryBatchRequest approved) => new(approved.OriginInstanceId, approved.BatchId, approved.BatchRevision, approved.ReviewId);
        static ApprovedBatchEntry PlaylistEntry(string name) => new(Guid.NewGuid(), 1,
            new ApprovedBatchChange.Playlist(new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), 1, null, null, name, []));
        LibraryBatchHandlers Batches(LiteDbDesktopPlaylistStore store, HttpClient client)
        {
            var files = new FileLibraryBatchImportStage(stage, music);
            return new(scope, store, new HttpApprovedLibraryBatchPublisher(client, client.BaseAddress!, library, tokens, files), files);
        }
        Task<HttpResponseMessage> Withdraw(HttpClient client, Guid operation, WithdrawApprovedLibraryBatchRequest intent) =>
            client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.WithdrawPath(library, operation), intent, Token);
        async Task<ApprovedLibraryBatchStatus> Status(HttpClient client, Guid operation) => Assert.IsType<ApprovedLibraryBatchStatus>(
            await client.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, operation), Token));
        async Task<string> Code(HttpResponseMessage response)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            return document.RootElement.GetProperty("code").GetString()!;
        }
        HttpClient Client(WebApplication host, string credential, bool loseWithdrawal = false)
        {
            var handler = CreateClientHandler(certificate);
            var client = new HttpClient(loseWithdrawal ? new LoseBatchWithdrawalReply(handler) : handler) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return client;
        }
        WebApplication Host()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, options, verifier, configured, configured);
        }
        static T Get<T>(Outcome<T> value) => Assert.IsType<Outcome<T>.Success>(value).Value;
    }

    /// <summary>Loses an acknowledged response after the host persists withdrawal.</summary>
    private sealed class LoseBatchWithdrawalReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (!response.IsSuccessStatusCode || !request.RequestUri!.AbsolutePath.EndsWith("/withdraw", StringComparison.Ordinal))
                return response;
            response.Dispose();
            throw new HttpRequestException("Simulated loss of the durable withdrawal reply.");
        }
    }
}
