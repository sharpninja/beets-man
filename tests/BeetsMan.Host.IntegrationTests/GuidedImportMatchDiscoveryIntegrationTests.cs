using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Beets;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies read-only discovery and, in the native lane, exact approved release application and recovery.</summary>
    /// <remarks>2026-11-24: Bind the SCR37 comparison for FR-BEETS-001#ac-import. The native lane sets BEETSMAN_TEST_BEETS and BEETSMAN_TEST_FFMPEG.</remarks>
    /// <remarks>2026-11-24: Bind the same pinned release and media expectations to FR-LIBRARY-002#ac-batch-commit.</remarks>
    [Fact]
    public async Task GuidedImportMatchDiscoveryPreviewsAndAppliesOnlyTheApprovedRelease()
    {
        var lookupBuilder = WebApplication.CreateSlimBuilder();
        lookupBuilder.Logging.ClearProviders();
        lookupBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        await using var lookup = lookupBuilder.Build();
        string? query = null;
        var changedProvider = 0;
        var lookups = new System.Collections.Concurrent.ConcurrentQueue<string>();
        lookup.MapGet("/ws/2/release", (HttpRequest request) =>
        {
            query = request.Query["query"].ToString();
            Assert.Contains("BeetsMan/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Results.Json(new { releases = new[] { ReleaseSummary(false), ReleaseSummary(true), ReleaseSummary(false) } });
        });
        lookup.MapGet("/ws/2/release/{releaseId}", (string releaseId) =>
        {
            lookups.Enqueue(releaseId);
            return releaseId == ImportMatchDiscoveryAssertions.FixtureRelease ? Results.Json(ReleaseDetails(false, Volatile.Read(ref changedProvider) != 0)) :
                releaseId == ImportMatchDiscoveryAssertions.AlternateRelease ? Results.Json(ReleaseDetails(true)) : Results.NotFound();
        });
        await lookup.StartAsync(Token);
        var server = new Uri(lookup.Urls.Single());
        using var service = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var provider = new MusicBrainzReleaseSearch(service, server, TimeProvider.System);

        var database = Path.Combine(root, "host.db");
        var nativeCatalog = Path.Combine(root, "beets.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var stageRoot = Path.Combine(root, "stage");
        var workRoot = Path.Combine(root, "work");
        var original = Directory.CreateDirectory(Path.Combine(root, "sources")).FullName;
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var sourceTitles = new[] { "North", "South" };
        var sourcePaths = sourceTitles.Select(title => Path.Combine(original, title + ".flac")).ToArray();
        var content = new byte[2][];
        for (var index = 0; index < sourcePaths.Length; index++)
        {
            if (ffmpeg is null)
                await File.WriteAllTextAsync(sourcePaths[index], "fixture " + sourceTitles[index], Token);
            else
                await GenerateAudioAsync(ffmpeg, sourcePaths[index], sourceTitles[index], index + 1, Token);
            content[index] = await File.ReadAllBytesAsync(sourcePaths[index], Token);
        }
        SeedHost(database);
        using (var db = new LiteDatabase(database))
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 41L });
        var identity = new AuthenticatedHostInstance(library, instance);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        var manifest = new BeginImportUploadRequest(instance, Guid.NewGuid(), [.. sourcePaths.Select((path, index) =>
            new ImportUploadFile(Path.GetFileName(path), content[index].Length, Convert.ToHexString(SHA256.HashData(content[index]))))]);

        string[] releaseKeys, releaseTitles;
        ImportMatchDetails selected;
        bool unavailable;
        Guid jobId, selectedId;
        await using (var host = beets is null ? LibraryHostStartup.Build(builder, new(library, database, music, stageRoot, workRoot, []),
                         new Verifier(identity, secret, null), credentials, credentials) : BuildMatchingHost())
        {
            await host.StartAsync(Token);
            var intake = host.Services.GetRequiredService<HostImportIntake>();
            Require(await intake.BeginAsync(library, identity, manifest, Token));
            for (var index = 0; index < content.Length; index++)
            {
                using var body = new MemoryStream(content[index]);
                Require(await intake.PutPartAsync(library, identity, manifest.UploadId, index, content[index].Length, body, _ => { }, Token));
            }
            var accepted = Require(await intake.StartAsync(library, identity, new(instance, Guid.NewGuid(), manifest.UploadId), Token));
            jobId = accepted.JobId;
            var reviews = host.Services.GetRequiredService<HostImportReview>();
            ImportJobSnapshot ready;
            if (beets is not null)
            {
                ready = await WaitForJob(host, phase => phase is ImportJobPhase.ReviewRequired or ImportJobPhase.Failed);
                Assert.True(ready.Phase == ImportJobPhase.ReviewRequired, $"Native preview ended in {ready.Phase} ({ready.FailureCode}).");
                Assert.Equal([ImportMatchDiscoveryAssertions.FixtureRelease, ImportMatchDiscoveryAssertions.AlternateRelease], lookups.ToArray());
                releaseKeys = [.. lookups.Select(id => "musicbrainz:" + id)];
            }
            else
            {
                var results = Require(await provider.SearchAsync("Fixture Album", "Fixture Band", Token));
                ImmutableArray<BeetsImportCandidate> candidates = [.. results.Select((release, index) => new BeetsImportCandidate(release.ProviderKey, release.Title, release.Artist,
                    new(release.Date, release.Country, release.TrackCount, index == 0,
                        [new(0, "North", index == 0 ? "North Revised" : null), new(1, "South", index == 0 ? "South Revised" : null)])))];
                ready = Require(await reviews.OfferAsync(library, accepted.JobId, manifest.UploadId, candidates, Token));
                releaseKeys = [.. candidates.Select(match => match.ProviderKey)];
            }
            var comparison = Assert.IsType<ImportAlbumReview>(ready.Review);
            releaseTitles = [.. comparison.Matches.Select(match => match.Title)];
            var rejected = await reviews.ChooseAsync(library, accepted.JobId, identity,
                new(instance, Guid.NewGuid(), comparison.ItemId, ready.Revision, ImportChoiceKind.UseMatch, comparison.Matches[1].MatchId), Token);
            Assert.Equal("incomplete-import-match", Assert.IsType<HostOutcome<ChooseImportReceipt>.Rejected>(rejected).Code);

            using var client = new HttpClient(CreateClientHandler(certificate));
            var gateway = new HttpImportIntakeHost(client, new Uri(host.Urls.Single()), library, new TokenProvider(library, instance, secret));
            var routes = new RequestDispatcher.Builder().RegisterQuery<GetHostLibraryState, HostLibraryState>(new AvailableImportLibrary(library));
            var dispatcher = new ImportIntakeHandlers(library, new ImportFilePicker(), gateway).Register(routes).Build();
            using var viewModel = new HostLibraryViewModel(new(dispatcher, new InlineScheduler(), instance), Guid.NewGuid(), library);
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenLibraryCommand.ExecuteAsync(null);
            await viewModel.InspectJobCommand.ExecuteAsync(accepted.JobId);
            var matches = Assert.IsType<ImportReviewItem>(viewModel.Job?.Review).Matches;
            selected = Assert.IsType<ImportMatchDetails>(matches[0].Details);
            selectedId = matches[0].MatchId;
            Assert.True(viewModel.ChooseImportMatchCommand.CanExecute(matches[0].MatchId));
            unavailable = !viewModel.ChooseImportMatchCommand.CanExecute(matches[1].MatchId);
            Assert.Equal(1, matches[1].Details?.ReleaseTrackCount);
        }

        var stagedPaths = Directory.GetFiles(stageRoot, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        ImportMatchDiscoveryAssertions.Verify(new(query!, releaseKeys, releaseTitles,
            selected.ReleaseDate, selected.Country, [.. selected.Tracks.Select(track => track.OriginalTitle)], [.. selected.Tracks.Select(track => track.ProposedTitle)],
            selected.CanApply, !unavailable, await SourcesRetained(), CatalogUnchanged()));

        if (beets is null)
            return;
        Volatile.Write(ref changedProvider, 1);
        ImportJobSnapshot failed;
        await using (var host = BuildMatchingHost())
        {
            await host.StartAsync(Token);
            using var client = new HttpClient(CreateClientHandler(certificate));
            using var viewModel = CreateViewModel(host, client);
            await viewModel.OpenLibraryCommand.ExecuteAsync(null);
            await viewModel.InspectJobCommand.ExecuteAsync(jobId);
            await viewModel.ChooseImportMatchCommand.ExecuteAsync(selectedId);
            Assert.Equal(jobId, viewModel.AcceptedJob?.JobId);
            failed = await WaitForJob(host, phase => phase is ImportJobPhase.Published or ImportJobPhase.Failed);
            Assert.Equal(ImportJobPhase.Failed, failed.Phase);
        }

        var noLiveEffects = CatalogUnchanged();
        bool noAttempt;
        using (var journal = new LiteDatabase(database))
        {
            var row = journal.GetCollection<BsonDocument>("library_jobs").FindById(jobId);
            noAttempt = !row["NativeAttempted"].IsBoolean || !row["NativeAttempted"].AsBoolean;
        }

        Volatile.Write(ref changedProvider, 0);
        var liveWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ImportJobSnapshot published;
        await using (var host = BuildMatchingHost(liveWrite))
        {
            await host.StartAsync(Token);
            using var client = new HttpClient(CreateClientHandler(certificate));
            using var viewModel = CreateViewModel(host, client);
            await viewModel.OpenLibraryCommand.ExecuteAsync(null);
            await viewModel.InspectJobCommand.ExecuteAsync(jobId);
            await viewModel.RetryJobCommand.ExecuteAsync(null);
            Assert.Equal(jobId, viewModel.AcceptedJob?.JobId);
            Assert.True(await liveWrite.Task.WaitAsync(TimeSpan.FromSeconds(30), Token));
            await host.StopAsync(Token);
        }
        using (var journal = new LiteDatabase(database))
        {
            var row = journal.GetCollection<BsonDocument>("library_jobs").FindById(jobId);
            Assert.True(row["NativeAttempted"].AsBoolean);
            Assert.True(row["NativePreparation"].IsString);
            Assert.Equal(nameof(ImportJobPhase.Running), row["Status"].AsString);
            Assert.Equal(41L, journal.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64);
        }
        await using (var host = BuildMatchingHost())
        {
            await host.StartAsync(Token);
            published = await WaitForJob(host, phase => phase is ImportJobPhase.Published or ImportJobPhase.Failed);
            Assert.True(published.Phase == ImportJobPhase.Published, $"The approved match ended in {published.Phase} ({published.FailureCode}).");
        }

        using (var readback = new LiteDatabase(database))
        {
            Assert.Equal([ImportMatchDiscoveryAssertions.FixtureRelease, ImportMatchDiscoveryAssertions.AlternateRelease,
                ImportMatchDiscoveryAssertions.FixtureRelease, ImportMatchDiscoveryAssertions.FixtureRelease], lookups.ToArray());
            var rows = readback.GetCollection<BsonDocument>("catalog_tracks").FindAll().OrderBy(row => row["TrackNumber"].AsInt32).ToArray();
            Assert.All(rows, row =>
            {
                Assert.Equal(1, row["DiscNumber"].AsInt32);
                Assert.Equal(2023, row["Year"].AsInt32);
            });
            var changedCopies = rows.Length == content.Length;
            for (var index = 0; index < rows.Length && changedCopies; index++)
            {
                var path = Path.Combine(music, rows[index]["RelativePath"].AsString.Replace('/', Path.DirectorySeparatorChar));
                var publishedBytes = await File.ReadAllBytesAsync(path, Token);
                changedCopies = !content[index].SequenceEqual(publishedBytes);
            }
            var config = Directory.GetFiles(Path.Combine(workRoot, "beets-import"), "config.yaml", SearchOption.AllDirectories).First();
            var releases = await ReadPublishedReleaseIds(beets, config, Token);
            ImportMatchApplyAssertions.Verify(new(failed.FailureCode!, failed.Retryable, noLiveEffects, noAttempt, jobId, published.JobId,
                published.CatalogRevision!.Value, [.. rows.Select(row => row["Title"].AsString)], await SourcesRetained(), changedCopies,
                releases.Length == 2 && releases.All(id => id == ImportMatchDiscoveryAssertions.FixtureRelease)));
        }
        await VerifyBatchMatchAsync();

        async Task VerifyBatchMatchAsync()
        {
            var batchDatabase = Path.Combine(root, "batch-match.db");
            var batchMusic = Directory.CreateDirectory(Path.Combine(root, "batch-music")).FullName;
            var batchStage = Path.Combine(root, "batch-stage");
            var batchWork = Path.Combine(root, "batch-work");
            SeedHost(batchDatabase);
            var matchBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            matchBuilder.Logging.ClearProviders();
            matchBuilder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            var settings = new BeetsImportOptions(beets!, Path.Combine(root, "beets-batch-match.db"), CreateCatalogIfMissing: true, Matching: new(ffmpeg!, server));
            var entry = new ApprovedBatchEntry(Guid.NewGuid(), 1, new ApprovedBatchChange.Import([.. manifest.Files.Select(file => new ApprovedBatchImportFile(Guid.NewGuid(), file))]));
            var approval = new ApproveLibraryBatchRequest(instance, Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), [entry]);
            var choice = Guid.NewGuid();
            await using (var host = LibraryHostStartup.Build(matchBuilder, new(library, batchDatabase, batchMusic, batchStage, batchWork, [], Beets: settings),
                new Verifier(identity, secret, null), credentials, credentials))
            {
                await host.StartAsync(Token);
                using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using var approved = await client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), approval, Token);
                approved.EnsureSuccessStatusCode();
                var import = Assert.IsType<ApprovedBatchChange.Import>(entry.Change);
                for (var index = 0; index < import.Files.Length; index++)
                {
                    using var body = new ByteArrayContent(content[index]);
                    using var uploaded = await client.PutAsync(ApprovedLibraryBatchRoutes.ImportPartPath(library, approval.OperationId, 1, index, import.Files[index].FileId), body, Token);
                    uploaded.EnsureSuccessStatusCode();
                }
                var offered = await WaitAsync(status => !status.ImportReviews.IsDefaultOrEmpty);
                var review = Assert.Single(offered.ImportReviews);
                var match = Assert.Single(review.Album.Matches, candidate => candidate.Comparison?.CanApply == true);
                Assert.Empty(Directory.EnumerateFileSystemEntries(batchMusic));
                using var chosen = await client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ImportChoicePath(library, approval.OperationId, 1),
                    new ChooseApprovedBatchImportRequest(instance, choice, entry.OperationId, review.Album.ItemId, review.Revision, ImportChoiceKind.UseMatch, match.MatchId), Token);
                chosen.EnsureSuccessStatusCode();
                Assert.Equal(1, (await WaitAsync(status => status.Phase == BatchPublicationPhase.Published)).CatalogRevision);

                async Task<ApprovedLibraryBatchStatus> WaitAsync(Predicate<ApprovedLibraryBatchStatus> ready)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    while (true)
                    {
                        var status = Assert.IsType<ApprovedLibraryBatchStatus>(await client.GetFromJsonAsync<ApprovedLibraryBatchStatus>(
                            ApprovedLibraryBatchRoutes.StatusPath(library, approval.OperationId), timeout.Token));
                        if (ready(status))
                            return status;
                        await Task.Delay(100, timeout.Token);
                    }
                }
            }
            using var readback = new LiteDatabase(batchDatabase);
            Assert.Equal(0, readback.GetCollection<BsonDocument>("library_jobs").Count());
            var journal = Assert.Single(readback.GetCollection<BsonDocument>("library_batch_publication_entries").FindAll());
            Assert.True(journal["PreparedImport"].IsString);
            var observed = Assert.IsType<ReconciledImportAlbum>(System.Text.Json.JsonSerializer.Deserialize<ReconciledImportAlbum>(journal["ObservedImport"].AsString));
            Assert.Equal(choice, observed.ChoiceOperationId);
            Assert.Equal(entry.OperationId, observed.UploadId);
            Assert.Equal("musicbrainz:" + ImportMatchDiscoveryAssertions.FixtureRelease, observed.ProviderKey);
            Assert.Equal(["North Revised", "South Revised"], observed.Tracks.Select(track => track.Title));
            var batchSources = Directory.GetFiles(batchStage, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(content.Length, batchSources.Length);
            for (var index = 0; index < content.Length; index++)
            {
                var stagedBytes = await File.ReadAllBytesAsync(batchSources[index], Token);
                var publishedBytes = await File.ReadAllBytesAsync(Path.Combine(batchMusic, observed.Tracks[index].RelativePath), Token);
                Assert.Equal(content[index], stagedBytes);
                Assert.False(content[index].SequenceEqual(publishedBytes));
            }
            Assert.True(await SourcesRetained());
        }

        async Task<bool> SourcesRetained()
        {
            var staged = await Task.WhenAll(stagedPaths.Select(path => File.ReadAllBytesAsync(path, Token)));
            var retained = await Task.WhenAll(sourcePaths.Select(path => File.ReadAllBytesAsync(path, Token)));
            return staged.Length == 2 && staged.Zip(content).All(pair => pair.First.SequenceEqual(pair.Second)) &&
                retained.Zip(content).All(pair => pair.First.SequenceEqual(pair.Second));
        }

        bool CatalogUnchanged()
        {
            using var local = new LiteDatabase(database);
            return !File.Exists(nativeCatalog) && !Directory.EnumerateFileSystemEntries(music).Any() &&
                local.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64 == 41L &&
                local.GetCollection<BsonDocument>("revision_changes").Count() == 0;
        }

        WebApplication BuildMatchingHost(TaskCompletionSource<bool>? pauseAfterWrite = null)
        {
            var local = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            local.Logging.ClearProviders();
            local.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            var settings = new BeetsImportOptions(beets!, nativeCatalog, CreateCatalogIfMissing: true, Matching: new(ffmpeg!, server));
            if (pauseAfterWrite is not null)
                local.Services.AddSingleton<IBeetsGuidedImport>(services => new PauseAfterMatchedWrite(
                    new BeetsGuidedImport(settings, music, workRoot, services.GetRequiredService<FileImportStage>(),
                        services.GetRequiredService<MusicBrainzReleaseSearch>()), pauseAfterWrite));
            return LibraryHostStartup.Build(local, new(library, database, music, stageRoot, workRoot, [], Beets: settings),
                new Verifier(identity, secret, null), credentials, credentials);
        }

        HostLibraryViewModel CreateViewModel(WebApplication host, HttpClient client)
        {
            var gateway = new HttpImportIntakeHost(client, new Uri(host.Urls.Single()), library, new TokenProvider(library, instance, secret));
            var routes = new RequestDispatcher.Builder().RegisterQuery<GetHostLibraryState, HostLibraryState>(new AvailableImportLibrary(library));
            var dispatcher = new ImportIntakeHandlers(library, new ImportFilePicker(), gateway).Register(routes).Build();
            var model = new HostLibraryViewModel(new(dispatcher, new InlineScheduler(), instance), Guid.NewGuid(), library);
            model.Activate(model.Scope.OwnerId);
            return model;
        }

        async Task<ImportJobSnapshot> WaitForJob(WebApplication host, Predicate<ImportJobPhase> ready)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            var intake = host.Services.GetRequiredService<HostImportIntake>();
            while (true)
            {
                var current = Require(await intake.ReadJobAsync(library, identity, jobId, limit.Token));
                if (ready(current.Phase))
                    return current;
                await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token);
            }
        }
    }

    /// <summary>Interrupts the worker after a real beets import but before catalog publication.</summary>
    private sealed class PauseAfterMatchedWrite(IBeetsGuidedImport inner, TaskCompletionSource<bool> written) : IBeetsGuidedImport
    {
        public Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token) => inner.DiscoverAsync(upload, token);
        public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) => inner.PrepareAsync(work, token);
        public Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token) => inner.ObserveAsync(work, token);

        public async Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token)
        {
            var result = await inner.ApplyAsync(work, token);
            written.TrySetResult(result is HostOutcome<bool>.Success { Value: true });
            if (result is HostOutcome<bool>.Success { Value: true })
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return result;
        }
    }

    private static async Task<string[]> ReadPublishedReleaseIds(string beets, string config, CancellationToken token)
    {
        var start = new ProcessStartInfo(beets) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["BEETSDIR"] = Path.GetDirectoryName(config)!;
        foreach (var argument in new[] { "-c", config, "list", "-f", "${mb_albumid}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var result = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await errors);
        return (await result).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static Dictionary<string, object?> ReleaseSummary(bool alternate) => new()
    {
        ["id"] = alternate ? ImportMatchDiscoveryAssertions.AlternateRelease : ImportMatchDiscoveryAssertions.FixtureRelease,
        ["title"] = alternate ? "Fixture Album (Live)" : "Fixture Album", ["date"] = "2023-04-21", ["country"] = "US",
        ["track-count"] = alternate ? 1 : 2, ["artist-credit"] = MatchArtistCredits()
    };

    private static Dictionary<string, object?> ReleaseDetails(bool alternate, bool changed = false)
    {
        var release = ReleaseSummary(alternate);
        var tracks = alternate ? new[] { MatchTrack(1, "North Live") } :
            new[] { MatchTrack(1, changed ? "North Changed After Approval" : "North Revised"), MatchTrack(2, "South Revised") };
        release["status"] = "Official";
        release["disambiguation"] = "";
        release["asin"] = null;
        release["text-representation"] = new { language = "eng", script = "Latn" };
        release["label-info"] = Array.Empty<object>();
        release["release-group"] = new Dictionary<string, object?>
        {
            ["id"] = "43fc11ab-b44a-4dc9-b380-fa6e0f8c1563", ["title"] = "Fixture Album", ["disambiguation"] = "",
            ["primary-type"] = "Album", ["secondary-types"] = Array.Empty<object>(), ["first-release-date"] = "2023-04-21"
        };
        release["media"] = new[] { new Dictionary<string, object?>
        {
            ["position"] = 1, ["title"] = "", ["format"] = "Digital Media", ["track-count"] = tracks.Length, ["tracks"] = tracks
        } };
        return release;
    }

    private static Dictionary<string, object?> MatchTrack(int position, string title) => new()
    {
        ["id"] = $"f51c49bc-dd26-4aed-996c-707c99ed4c0{position}", ["position"] = position, ["number"] = position.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["title"] = title, ["length"] = 100, ["artist-credit"] = MatchArtistCredits(),
        ["recording"] = new Dictionary<string, object?>
        {
            ["id"] = $"a9d3bec2-02c9-49c1-ac7a-66f03d6a420{position}", ["title"] = title, ["length"] = 100,
            ["artist-credit"] = MatchArtistCredits(), ["video"] = false, ["disambiguation"] = "", ["aliases"] = Array.Empty<object>()
        }
    };

    private static object[] MatchArtistCredits() => [new Dictionary<string, object?>
    {
        ["name"] = "Fixture Band", ["joinphrase"] = "", ["artist"] = new Dictionary<string, object?>
        {
            ["id"] = "43e9f1fc-7bea-45d8-bb98-0798e9422b5d", ["name"] = "Fixture Band", ["sort-name"] = "Fixture Band",
            ["aliases"] = Array.Empty<object>()
        }
    }];
}
