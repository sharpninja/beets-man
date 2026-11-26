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
    /// <summary>Uses the desktop CQRS, HTTPS and both databases to record exact guided choices across restart.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-004 and TEST-BEETS-002 to private batch import review.</remarks>
    /// <remarks>2026-11-26: Verify private guided import and revision-bound approval for FR-BEETS-001#ac-import and FR-LIBRARY-002#ac-batch-review.</remarks>
    [Fact]
    public async Task GuidedBatchReviewsPersistExplicitChoicesWithoutPublishingCatalog()
    {
        const string foreignToken = "guided-batch-foreign";
        var foreign = Guid.NewGuid();
        var scope = new LibraryBatchScope(library, instance);
        var read = new RequestContext(Guid.NewGuid(), instance);
        var approval = Command();
        var choice = Command();
        var hostDb = Path.Combine(root, "review-host.db");
        var desktopDb = Path.Combine(root, "review-desktop.db");
        var hostStage = Path.Combine(root, "host-private");
        var localStage = Path.Combine(root, "desktop-private");
        var work = Path.Combine(root, "native-work");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var sources = Directory.CreateDirectory(Path.Combine(root, "picked")).FullName;
        var names = new[] { "One.flac", "Two.flac", "Keep.flac", "Skip.flac" };
        var originals = names.Select(name => Path.Combine(sources, name)).ToArray();
        foreach (var (name, path) in names.Zip(originals))
            await File.WriteAllTextAsync(path, "reviewed bytes for " + name, Token);
        SeedHost(hostDb);
        using (var db = new LiteDatabase(hostDb))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = foreign, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 30L });
        }
        var seen = new ConcurrentQueue<string>();
        using var certificate = CreateCertificate();
        var configured = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [foreignToken] = new(library, foreign) });
        var credentials = new TokenProvider(library, instance, secret);
        var provider = Guid.NewGuid();
        var options = new BeetsImportOptions(Environment.ProcessPath!, Path.Combine(root, "beets.db"), CreateCatalogIfMissing: true);
        LibraryBatchReview frozen;
        bool incomplete;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: true))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var http = new HttpClient(new ApprovedImportTransport(CreateClientHandler(certificate), loseFirst: true));
            var batch = Batch(desktop, address, http);
            Get(await batch.HandleAsync(new SetLibraryBatchMode(true, 0), Command(), Token));
            await Stage([originals[0], originals[1]]);
            await Stage([originals[2]]);
            await Stage([originals[3]]);
            Get(await batch.StageAsync(new LibraryBatchChange.Playlist(Guid.NewGuid(), 1, null, "Wait for all imports", []), Command(), Token));
            frozen = Get(await batch.HandleAsync(new GetLibraryBatchReview(), read, Token));
            var failed = await batch.HandleAsync(new CommitLibraryBatch(new(frozen.Version, frozen.ReviewId!.Value)), approval, Token);
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(failed);
            var partial = Get(await batch.HandleAsync(new GetLibraryBatchImportReviews(), read, Token));
            incomplete = partial.ApprovalOperationId == approval.OperationId && partial.Entries.IsEmpty && seen.IsEmpty;

            async Task Stage(ImmutableArray<string> paths)
            {
                var imports = new ImportIntakeHandlers(library, new ApprovedImportPicker(paths), new HttpImportIntakeHost(http, address, library, credentials), batch);
                var selected = Get(await imports.HandleAsync(new SelectImportSources(Guid.NewGuid()), Command(), Token));
                var staged = Get(await imports.HandleAsync(new UploadImportSources(library, selected.SelectionToken), Command(), Token));
                Assert.NotNull(staged.LocalBatch);
            }
        }

        ChooseLibraryBatchImportAction selectedAction;
        LibraryBatchImportChoiceReceipt selectedReceipt;
        bool projection;
        bool foreignDenied;
        bool incompleteDenied;
        bool staleDenied;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: false))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var http = new HttpClient(CreateClientHandler(certificate));
            using var raw = Client(address, secret);
            using var outsider = Client(address, foreignToken);
            var routes = Batch(desktop, address, http).Register(new()).Build();
            Assert.IsType<Outcome<LibraryBatchCommitReceipt>.DependencyFailure>(await routes.SendAsync(new ResumeLibraryBatchCommit(), Command(), Token));
            var reviews = await Ready(routes);
            var first = reviews.Entries[0];
            var good = Assert.Single(first.Album.Matches, item => item.Details?.CanApply == true);
            var bad = Assert.Single(first.Album.Matches, item => item.Details?.CanApply == false);
            var rawStatus = await raw.GetStringAsync(ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), Token);
            projection = reviews.Version == frozen.Version && reviews.Entries.Select(entry => entry.Position).SequenceEqual([1, 2, 3]) &&
                first.Album.SourceNames.SequenceEqual(names.Take(2)) && good.Details!.Tracks.Select(track => track.ProposedTitle).SequenceEqual(["Matched 1", "Matched 2"]) &&
                !rawStatus.Contains(provider.ToString("D"), StringComparison.OrdinalIgnoreCase) && !rawStatus.Contains(hostStage, StringComparison.Ordinal);

            using var unauthorized = await outsider.GetAsync(ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), Token);
            using var wrongOwner = await outsider.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ImportChoicePath(library, approval.OperationId, first.Position),
                new ChooseApprovedBatchImportRequest(foreign, Guid.NewGuid(), first.EntryOperationId, first.Album.ItemId, first.Revision, ImportChoiceKind.KeepTags), Token);
            foreignDenied = unauthorized.StatusCode == HttpStatusCode.NotFound && wrongOwner.StatusCode == HttpStatusCode.NotFound;

            selectedAction = Choose(first, new ImportAction.UseMatch(good.MatchId));
            var incompleteOutcome = await routes.SendAsync(selectedAction with { Action = new ImportAction.UseMatch(bad.MatchId) }, Command(), Token);
            incompleteDenied = incompleteOutcome is Outcome<LibraryBatchImportChoiceReceipt>.Invalid invalid && invalid.Issues.Any(issue => issue.Code == "incomplete-import-match");
            staleDenied = await routes.SendAsync(selectedAction with { ReviewItemId = Guid.NewGuid() }, Command(), Token) is
                Outcome<LibraryBatchImportChoiceReceipt>.Conflict { Code: "stale-batch-import-review" };
            selectedReceipt = Get(await routes.SendAsync(selectedAction, choice, Token));
            Get(await routes.SendAsync(Choose(reviews.Entries[1], new ImportAction.KeepTags()), Command(), Token));
            await host.StopAsync(Token);
        }

        bool persisted;
        bool replay;
        bool changedIntent;
        bool chosen;
        bool untouched;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopDb, allowCreate: false))
        await using (var host = Host())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var http = new HttpClient(CreateClientHandler(certificate));
            using var raw = Client(address, secret);
            var routes = Batch(desktop, address, http).Register(new()).Build();
            var reviews = Get(await routes.QueryAsync(new GetLibraryBatchImportReviews(), read, Token));
            persisted = reviews.Entries[0].Action == selectedAction.Action && reviews.Entries[0].Revision == selectedReceipt.Revision &&
                reviews.Entries[1].Action is ImportAction.KeepTags && reviews.Entries[2].Action is null;
            replay = Get(await routes.SendAsync(selectedAction, choice, Token)) == selectedReceipt;
            changedIntent = await routes.SendAsync(selectedAction with { Action = new ImportAction.Skip() }, choice, Token) is
                Outcome<LibraryBatchImportChoiceReceipt>.Conflict { Code: "operation-intent-mismatch" };
            var beforeChoice = await raw.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), Token);
            Assert.True(beforeChoice is { Phase: BatchPublicationPhase.Accepted, CatalogRevision: null });
            Get(await routes.SendAsync(Choose(reviews.Entries[2], new ImportAction.Skip()), Command(), Token));
            var decided = Get(await routes.QueryAsync(new GetLibraryBatchImportReviews(), read, Token));
            chosen = decided.Entries.Select(entry => entry.Action?.GetType()).SequenceEqual([typeof(ImportAction.UseMatch), typeof(ImportAction.KeepTags), typeof(ImportAction.Skip)]);
            var status = await raw.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), Token);
            var feed = await raw.GetFromJsonAsync<CatalogFeedPage>(CatalogReplicationRoutes.ChangesPath(library, 30, null, 10), Token);
            untouched = status is { Phase: BatchPublicationPhase.Accepted, CatalogRevision: null } && feed!.Revisions.IsEmpty && Directory.GetFileSystemEntries(music).Length == 0;
        }
        using var final = new LiteDatabase(hostDb);
        BatchImportReviewAssertions.Verify(new(incomplete, seen.Order(StringComparer.Ordinal).SequenceEqual(names.Select(name => "reviewed bytes for " + name)
            .Order(StringComparer.Ordinal)), projection, foreignDenied, incompleteDenied, staleDenied, persisted, replay, changedIntent, chosen,
            untouched && final.GetCollection<BsonDocument>("saved_playlists").Count() == 0, final.GetCollection<BsonDocument>("library_jobs").Count() == 0));

        CommandContext Command() => new(read, Guid.NewGuid());
        ChooseLibraryBatchImportAction Choose(LibraryBatchImportReview entry, ImportAction action) => new(frozen.Version, entry.Position, entry.EntryOperationId,
            entry.Album.ItemId, entry.Revision, action);
        LibraryBatchHandlers Batch(LiteDbDesktopPlaylistStore db, Uri address, HttpClient http)
        {
            var copies = new FileLibraryBatchImportStage(localStage, music);
            return new(scope, db, new HttpApprovedLibraryBatchPublisher(http, address, library, credentials, copies), copies);
        }
        WebApplication Host()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            builder.Services.AddSingleton<Func<Guid, IBeetsGuidedImport>>(id => new PrivateReviewFixture(new FileImportStage(hostStage, id), provider, seen));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, hostStage, work, [], Beets: options), verifier, configured, configured);
        }
        HttpClient Client(Uri address, string token)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        async Task<LibraryBatchImportReviews> Ready(IRequestDispatcher routes)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                var result = Get(await routes.QueryAsync(new GetLibraryBatchImportReviews(), read, timeout.Token));
                if (result.Entries.Length == 3)
                    return result;
                await Task.Delay(100, timeout.Token);
            }
        }
        static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    /// <summary>Uses real private copy verification and a deterministic guided provider for the integration fixture.</summary>
    private sealed class PrivateReviewFixture(FileImportStage stage, Guid provider, ConcurrentQueue<string> seen) : IBeetsGuidedImport
    {
        public async Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token)
        {
            var staged = Assert.IsType<HostOutcome<VerifiedImportAlbum>.Success>(await stage.OpenVerifiedAlbumAsync(upload, token)).Value;
            foreach (var path in staged.Paths)
                seen.Enqueue(await File.ReadAllTextAsync(path, token));
            var tracks = upload.Files.Select((file, index) => new ImportTrackComparison(index, file.Name, $"Matched {index + 1}")).ToImmutableArray();
            var release = new ApprovedBeetsMatch(provider, "Reviewed album", "Artist", "2026", "11", "24", "US",
                [.. tracks.Select(track => new ApprovedBeetsTrack(Guid.NewGuid(), track.ProposedTitle!, "Artist"))]);
            return new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success([
                new($"musicbrainz:{provider:D}", "Reviewed album", "Artist", new("2026-11-24", "US", tracks.Length, true, tracks), release),
                new("private-incomplete", "Incomplete edition", "Artist", new(null, null, tracks.Length + 1, false, tracks))]);
        }
        public Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token) =>
            Task.FromResult<HostOutcome<NativeImportObservation>>(new HostOutcome<NativeImportObservation>.Unavailable("review-fixture-holds-native"));
        public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) => throw NativeWrite();
        public Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token) => throw NativeWrite();
        private static InvalidOperationException NativeWrite() => new("The review checkpoint must not begin native execution.");
    }
}
