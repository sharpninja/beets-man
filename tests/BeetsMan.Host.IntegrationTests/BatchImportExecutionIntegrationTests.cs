using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Host.Adapters.Beets;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Recovers an interrupted native import and publishes the whole batch once to a registered peer.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 and TEST-BEETS-002 to HTTPS, LiteDB, and native beets when configured.</remarks>
    /// <remarks>2026-11-26: Verify approved batch publication and recovery after interruption for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
    [Fact]
    public async Task GuidedBatchExecutionRecoversBeforePublishingOneRevision()
    {
        var hostDb = Path.Combine(root, "execute-batch-host.db");
        var desktopDb = Path.Combine(root, "execute-batch-desktop.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "execute-music")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "execute-sources")).FullName;
        var stage = Path.Combine(root, "execute-stage");
        var desktopStage = new FileLibraryBatchImportStage(Path.Combine(root, "execute-desktop-stage"), music);
        var workRoot = Path.Combine(root, "execute-work");
        var keep = Path.Combine(source, "Keep.flac");
        var skip = Path.Combine(source, "Skip.flac");
        var nativeCatalog = Path.Combine(root, "execute-beets.db");
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        if (ffmpeg is null)
        {
            await File.WriteAllTextAsync(keep, "keep fixture", Token);
            await File.WriteAllTextAsync(skip, "skip fixture", Token);
        }
        else
        {
            await GenerateAudioAsync(ffmpeg, keep, "Keep", 1, Token);
            await GenerateAudioAsync(ffmpeg, skip, "Skip", 2, Token);
        }
        var original = await File.ReadAllBytesAsync(keep, Token);
        SeedHost(hostDb);
        var peer = Guid.NewGuid();
        const string peerToken = "guided-batch-execution-peer";
        using (var db = new LiteDatabase(hostDb))
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [peerToken] = new(library, peer) });
        var credentials = new TokenProvider(library, instance, secret);
        var configured = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var options = new BeetsImportOptions(beets ?? Environment.ProcessPath!, nativeCatalog, CreateCatalogIfMissing: true);
        var nativeCalls = new ConcurrentQueue<ImportChoiceKind?>();
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RequestContext(Guid.NewGuid(), instance);
        var approval = Command();
        var openingSkip = Command();
        var earlyWrites = 0;
        long interruptedRevision;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: true))
        await using (var host = BuildHost(interrupt: true))
        {
            await host.StartAsync(Token);
            using var http = Client(host, secret);
            using var recipient = Client(host, peerToken);
            var batch = Batch(desktop, http);
            Get(await batch.HandleAsync(new SetLibraryBatchMode(true, 0), Command(), Token));
            await StageAsync(batch, http, skip);
            await StartAsync(batch, openingSkip);
            var first = Assert.Single((await ReviewsAsync(batch, 1)).Entries);
            await ChooseAsync(batch, first, new ImportAction.Skip());
            var empty = await FinishAsync(batch);
            Assert.Equal(0, empty.CatalogRevision);
            Assert.Empty((await FeedAsync(recipient)).Revisions);
            Assert.Empty(nativeCalls);

            await StageAsync(batch, http, keep);
            await StageAsync(batch, http, skip);
            Get(await batch.StageAsync(new LibraryBatchChange.Playlist(Guid.NewGuid(), 1, null, "Committed with import", []), Command(), Token));
            await StartAsync(batch, approval);
            var reviews = await ReviewsAsync(batch, 2);
            await ChooseAsync(batch, reviews.Entries[0], new ImportAction.KeepTags());
            await Task.Delay(TimeSpan.FromMilliseconds(1200), Token);
            earlyWrites = nativeCalls.Count;
            Assert.Empty(Directory.GetFileSystemEntries(music));
            Assert.Equal(0, (await FeedAsync(recipient)).UpperRevision);
            await ChooseAsync(batch, reviews.Entries[1], new ImportAction.Skip());
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            var live = Assert.Single(Directory.EnumerateFiles(music, "*.flac", SearchOption.AllDirectories));
            Assert.Equal(original, await File.ReadAllBytesAsync(live, Token));
            var tooLate = Assert.IsType<Outcome<LibraryBatchMode>.Conflict>(await batch.HandleAsync(new RevertLibraryBatch(reviews.Version), Command(), Token));
            Assert.Equal("library-batch-commit-in-progress", tooLate.Code);
            Assert.True(Get(await batch.HandleAsync(new GetLibraryBatchMode(), request, Token)).IsCommitting);
            interruptedRevision = (await FeedAsync(recipient)).UpperRevision;
            await host.StopAsync(Token);
        }

        CatalogFeedRevision published;
        long afterSkipOnly;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: false))
        await using (var host = BuildHost(interrupt: false))
        {
            await host.StartAsync(Token);
            using var http = Client(host, secret);
            using var recipient = Client(host, peerToken);
            var batch = Batch(desktop, http);
            var receipt = await FinishAsync(batch);
            Assert.Equal(approval.OperationId, receipt.OperationId);
            published = Assert.Single((await FeedAsync(recipient)).Revisions);
            Assert.Equal(approval.OperationId, published.OperationId);
            Assert.Equal(receipt.CatalogRevision, published.Revision);
            Assert.Equal(instance, published.OriginInstanceId);
            Assert.Contains(published.Changes, change => change.Entity is ReplicaAlbum { Title: "Fixture Album" });
            Assert.Contains(published.Changes, change => change.Entity is ReplicaArtist { Name: "Fixture Band" });
            Assert.Contains(published.Changes, change => change.Entity is ReplicaPlaylist { Name: "Committed with import" });
            await StageAsync(batch, http, skip);
            await StartAsync(batch, Command());
            var skipped = Assert.Single((await ReviewsAsync(batch, 1)).Entries);
            await ChooseAsync(batch, skipped, new ImportAction.Skip());
            Assert.Equal(receipt.CatalogRevision, (await FinishAsync(batch)).CatalogRevision);
            var feed = await FeedAsync(recipient);
            afterSkipOnly = feed.UpperRevision;
            Assert.Single(feed.Revisions);
        }
        using var final = new LiteDatabase(hostDb);
        BatchImportExecutionAssertions.Verify(new(earlyWrites, interruptedRevision, nativeCalls.Count(choice => choice == ImportChoiceKind.KeepTags),
            nativeCalls.Count(choice => choice == ImportChoiceKind.Skip), published.Revision, published.Changes.Count(change => change.Kind == CatalogEntityKind.Track),
            published.Changes.Count(change => change.Kind == CatalogEntityKind.Playlist), final.GetCollection<BsonDocument>("library_jobs").Count(), afterSkipOnly));
        Assert.Equal(original, await File.ReadAllBytesAsync(keep, Token));
        Assert.True(File.Exists(skip));

        CommandContext Command() => new(request, Guid.NewGuid());
        HttpClient Client(WebApplication host, string token)
        {
            var http = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }
        LibraryBatchHandlers Batch(LiteDbDesktopPlaylistStore desktop, HttpClient http) =>
            new(new(library, instance), desktop, new HttpApprovedLibraryBatchPublisher(http, http.BaseAddress!, library, credentials, desktopStage), desktopStage);
        async Task StageAsync(LibraryBatchHandlers batch, HttpClient http, string path)
        {
            var handler = new ImportIntakeHandlers(library, new ApprovedImportPicker([path]), new HttpImportIntakeHost(http, http.BaseAddress!, library, credentials), batch);
            var selected = Get(await handler.HandleAsync(new SelectImportSources(Guid.NewGuid()), Command(), Token));
            Assert.NotNull(Get(await handler.HandleAsync(new UploadImportSources(library, selected.SelectionToken), Command(), Token)).LocalBatch);
        }
        async Task StartAsync(LibraryBatchHandlers batch, CommandContext operation)
        {
            var review = Get(await batch.HandleAsync(new GetLibraryBatchReview(), request, Token));
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(await batch.HandleAsync(
                new CommitLibraryBatch(new(review.Version, review.ReviewId!.Value), KeepModeEnabled: true), operation, Token));
        }
        async Task<LibraryBatchImportReviews> ReviewsAsync(LibraryBatchHandlers batch, int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var reviews = Get(await batch.HandleAsync(new GetLibraryBatchImportReviews(), request, timeout.Token));
                if (reviews.Entries.Length == count)
                    return reviews;
                await Task.Delay(100, timeout.Token);
            }
        }
        async Task ChooseAsync(LibraryBatchHandlers batch, LibraryBatchImportReview entry, ImportAction action)
        {
            var review = Get(await batch.HandleAsync(new GetLibraryBatchImportReviews(), request, Token));
            Get(await batch.HandleAsync(new ChooseLibraryBatchImportAction(review.Version, entry.Position, entry.EntryOperationId,
                entry.Album.ItemId, entry.Revision, action), Command(), Token));
        }
        async Task<LibraryBatchCommitReceipt> FinishAsync(LibraryBatchHandlers batch)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var result = await batch.HandleAsync(new ResumeLibraryBatchCommit(), Command(), timeout.Token);
                if (result is Outcome<LibraryBatchCommitReceipt>.Success success)
                    return success.Value;
                Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(result);
                await Task.Delay(100, timeout.Token);
            }
        }
        async Task<CatalogFeedPage> FeedAsync(HttpClient http) => Assert.IsType<CatalogFeedPage>(await http.GetFromJsonAsync<CatalogFeedPage>(
            CatalogReplicationRoutes.ChangesPath(library, 0, null, 10), Token));
        WebApplication BuildHost(bool interrupt)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            builder.Services.AddSingleton<Func<Guid, IBeetsGuidedImport>>(approvalId => new InterruptBatchImport(beets is null
                ? new ScriptedNativeImport(music, [original]) : new BeetsGuidedImport(options, music, workRoot, new FileImportStage(stage, approvalId)),
                nativeCalls, interrupt ? interrupted : null));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, stage, workRoot, [], Beets: options), verifier, configured, configured);
        }
        static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    /// <summary>Interrupts after the actual external write and before its journal readback.</summary>
    private sealed class InterruptBatchImport(IBeetsGuidedImport inner, ConcurrentQueue<ImportChoiceKind?> calls, TaskCompletionSource? interrupt) : IBeetsGuidedImport
    {
        public Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token) => inner.DiscoverAsync(upload, token);
        public Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token) => inner.ObserveAsync(work, token);
        public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) => inner.PrepareAsync(work, token);

        public async Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token)
        {
            calls.Enqueue(work.Choice);
            var result = await inner.ApplyAsync(work, token);
            if (interrupt is not null)
            {
                Assert.True(Assert.IsType<HostOutcome<bool>.Success>(result).Value);
                interrupt.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return result;
        }
    }
}
