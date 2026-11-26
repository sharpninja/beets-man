using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
    /// <summary>Verifies that approved private copies resume at the missing ordinal after both stores restart.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 to FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-commit.</remarks>
    [Fact]
    public async Task ApprovedImportTransfersOnlyMissingVerifiedCopiesAfterRestart()
    {
        const string peerToken = "approved-batch-import-peer";
        var peer = Guid.NewGuid();
        var scope = new LibraryBatchScope(library, instance);
        var origin = new RequestContext(Guid.NewGuid(), instance);
        var approval = Command();
        var hostDb = Path.Combine(root, "batch-copy-host.db");
        var localDb = Path.Combine(root, "batch-copy-desktop.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "library-media")).FullName;
        var pickerRoot = Directory.CreateDirectory(Path.Combine(root, "picked-files")).FullName;
        var hostStage = Path.Combine(root, "host-private");
        var desktopStage = Path.Combine(root, "desktop-private");
        var paths = new[] { Path.Combine(pickerRoot, "One.flac"), Path.Combine(pickerRoot, "Two.flac") };
        for (var index = 0; index < paths.Length; index++)
            await File.WriteAllTextAsync(paths[index], ApprovedBatchImportAssertions.ReviewedBytes[index], Token);
        SeedHost(hostDb);
        using (var db = new LiteDatabase(hostDb))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 30L });
        }
        using var certificate = CreateCertificate();
        var configured = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [peerToken] = new(library, peer) });
        var credentials = new TokenProvider(library, instance, secret);
        LibraryBatchEntry entry;
        LibraryBatchChange.Import manifest;
        ApproveLibraryBatchRequest firstSubmission;
        bool noEarly;
        bool firstLost;
        bool badDigest;
        bool outOfScope;
        bool directIsolated;
        int[] confirmedAfterLoss;
        int[] sent;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: true))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var direct = Client(address, secret);
            using var foreign = Client(address, peerToken);
            using var transport = new ApprovedImportTransport(CreateClientHandler(certificate), loseFirst: true);
            using var http = new HttpClient(transport);
            var copies = Copies();
            var batches = Batch(desktop, address, http, copies);
            var imports = new ImportIntakeHandlers(library, new ApprovedImportPicker([.. paths]),
                new HttpImportIntakeHost(http, address, library, credentials), batches);
            Success(await batches.HandleAsync(new SetLibraryBatchMode(true, 0), Command(), Token));
            var selection = Success(await imports.HandleAsync(new SelectImportSources(Guid.NewGuid()), Command(), Token));
            var staged = Success(await imports.HandleAsync(new UploadImportSources(library, selection.SelectionToken), Command(), Token));
            Assert.NotNull(staged.LocalBatch);
            Success(await batches.StageAsync(new LibraryBatchChange.Playlist(Guid.NewGuid(), 1, null, "Approved once all effects finish", []), Command(), Token));
            var review = Success(await batches.HandleAsync(new GetLibraryBatchReview(), origin, Token));
            entry = review.Entries[0];
            manifest = Assert.IsType<LibraryBatchChange.Import>(entry.Change);
            Assert.Equal(["One.flac", "Two.flac"], manifest.Files.Select(file => file.Name));
            using var early = await Put(direct, manifest.Files[0].FileId, 0, ApprovedBatchImportAssertions.ReviewedBytes[0]);
            noEarly = early.StatusCode == HttpStatusCode.NotFound && transport.Submissions.Count == 0 && transport.Copies.Count == 0 &&
                !Directory.Exists(Path.Combine(hostStage, "approved-batch-imports"));

            var outcome = await batches.HandleAsync(new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value)), approval, Token);
            firstLost = outcome is Outcome<LibraryBatchCommitReceipt>.DependencyFailure { Code: "library-batch-import-host-unreachable", Retryable: true } &&
                Success(await batches.HandleAsync(new GetLibraryBatchMode(), origin, Token)).IsCommitting;
            firstSubmission = Assert.Single(transport.Submissions);
            var status = await Status(direct);
            Assert.Equal(2, status.ImportPartCount);
            confirmedAfterLoss = [.. status.TransferredImports.Select(file => file.Index)];
            sent = [.. transport.Copies];

            using var wrong = await Put(direct, manifest.Files[1].FileId, 1, new string('X', (int)manifest.Files[1].Size));
            var afterReject = await Status(direct);
            badDigest = wrong.StatusCode == HttpStatusCode.BadRequest && afterReject.TransferredImports.Length == 1;
            using var wrongId = await Put(direct, Guid.NewGuid(), 1, ApprovedBatchImportAssertions.ReviewedBytes[1]);
            using var wrongOwner = await Put(foreign, manifest.Files[1].FileId, 1, ApprovedBatchImportAssertions.ReviewedBytes[1]);
            outOfScope = wrongId.StatusCode == HttpStatusCode.NotFound && wrongOwner.StatusCode == HttpStatusCode.NotFound;

            var directFile = manifest.Files[0];
            using var begin = await direct.PostAsJsonAsync(ImportIntakeRoutes.BeginPath(library), new BeginImportUploadRequest(instance, entry.OperationId,
                [new(directFile.Name, directFile.Size, directFile.Sha256)]), Token);
            using var start = await direct.PostAsJsonAsync(ImportIntakeRoutes.StartPath(library), new StartImportRequest(instance, Guid.NewGuid(), entry.OperationId), Token);
            directIsolated = begin.IsSuccessStatusCode && start.StatusCode == HttpStatusCode.BadRequest;
        }

        foreach (var path in paths)
            await File.WriteAllTextAsync(path, "original outside staging was edited", Token);

        bool sameApproval;
        bool retainedLocal;
        bool unpublished;
        int[] verifiedOrdinals;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(localDb, allowCreate: false))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var direct = Client(address, secret);
            using var foreign = Client(address, peerToken);
            using var transport = new ApprovedImportTransport(CreateClientHandler(certificate), loseFirst: false);
            using var http = new HttpClient(transport);
            var copies = Copies();
            var batches = Batch(desktop, address, http, copies);
            var resumed = await batches.HandleAsync(new ResumeLibraryBatchCommit(), Command(), Token);
            Assert.Equal("library-batch-publication-pending", Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(resumed).Code);
            var replayed = Assert.Single(transport.Submissions);
            sameApproval = replayed.OperationId == approval.OperationId && replayed.ReviewId == firstSubmission.ReviewId &&
                replayed.BatchRevision == firstSubmission.BatchRevision && replayed.Entries.Select(item => item.OperationId)
                    .SequenceEqual(firstSubmission.Entries.Select(item => item.OperationId));
            sent = [.. sent, .. transport.Copies];
            var status = await Status(direct);
            verifiedOrdinals = [.. status.TransferredImports.Select(item => item.Index)];
            Assert.Equal(manifest.Files.Select(file => file.FileId), status.TransferredImports.Select(item => item.FileId));
            Assert.All(status.TransferredImports, item => Assert.Equal(entry.OperationId, item.EntryOperationId));
            var changes = await foreign.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 30, null, 10), Token);
            var notifications = await foreign.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token);
            unpublished = status.Phase == BatchPublicationPhase.Accepted && status.CatalogRevision is null && changes!.Revisions.IsEmpty &&
                notifications!.PendingRevisions.IsEmpty && Directory.GetFileSystemEntries(music).Length == 0;
            retainedLocal = Success(await batches.HandleAsync(new GetLibraryBatchMode(), origin, Token)).IsCommitting;
            for (var index = 0; index < manifest.Files.Length; index++)
            {
                await using var pinned = Success(await copies.OpenVerifiedAsync(scope, entry.OperationId, manifest.Files[index], Token));
                using var reader = new StreamReader(pinned, Encoding.UTF8, leaveOpen: true);
                retainedLocal &= await reader.ReadToEndAsync(Token) == ApprovedBatchImportAssertions.ReviewedBytes[index];
            }
        }

        var hostCopies = Path.Combine(hostStage, "approved-batch-imports", library.ToString("N"), instance.ToString("N"),
            approval.OperationId.ToString("N"), entry.OperationId.ToString("N"));
        var contents = new List<string>();
        foreach (var path in Directory.GetFiles(hostCopies).Order(StringComparer.Ordinal))
            contents.Add(await File.ReadAllTextAsync(path, Token));
        using var final = new LiteDatabase(hostDb);
        unpublished &= final.GetCollection<BsonDocument>("saved_playlists").Count() == 0 && final.GetCollection<BsonDocument>("library_jobs").Count() == 0;
        ApprovedBatchImportAssertions.Verify(new(noEarly, firstLost, confirmedAfterLoss, sent, verifiedOrdinals, contents.ToArray(), badDigest,
            outOfScope, directIsolated, sameApproval, retainedLocal, unpublished));

        CommandContext Command() => new(origin, Guid.NewGuid());
        FileLibraryBatchImportStage Copies() => new(desktopStage, music);
        LibraryBatchHandlers Batch(LiteDbDesktopPlaylistStore desktop, Uri address, HttpClient client, FileLibraryBatchImportStage copies) =>
            new(scope, desktop, new HttpApprovedLibraryBatchPublisher(client, address, library, credentials, copies), copies);
        async Task<ApprovedLibraryBatchStatus> Status(HttpClient client) => (await client.GetFromJsonAsync<ApprovedLibraryBatchStatus>(
            ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), Token))!;
        async Task<HttpResponseMessage> Put(HttpClient client, Guid file, int index, string data)
        {
            using var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
            bytes.Headers.ContentType = new("application/octet-stream");
            return await client.PutAsync(ApprovedLibraryBatchRoutes.ImportPartPath(library, approval.OperationId, 1, index, file), bytes, Token);
        }
        HttpClient Client(Uri address, string token)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        WebApplication Host()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, hostStage, Path.Combine(root, "host-work"), []), verifier,
                configured, configured);
        }
        static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class ApprovedImportPicker(ImmutableArray<string> paths) : IImportSourcePicker
    {
        public Task<ImmutableArray<string>> PickFilesAsync(Guid ownerId, CancellationToken token) => Task.FromResult(paths);
    }

    private sealed class ApprovedImportTransport(HttpMessageHandler inner, bool loseFirst) : DelegatingHandler(inner)
    {
        public List<ApproveLibraryBatchRequest> Submissions { get; } = [];
        public List<int> Copies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
                Submissions.Add((await request.Content!.ReadFromJsonAsync<ApproveLibraryBatchRequest>(token))!);
            else if (request.Method == HttpMethod.Put)
                Copies.Add(int.Parse(request.RequestUri!.Segments[^2].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture));
            var response = await base.SendAsync(request, token);
            if (!loseFirst || request.Method != HttpMethod.Put || Copies.Count != 1 || !response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw new HttpRequestException("Verified copy reply interrupted.");
        }
    }
}
