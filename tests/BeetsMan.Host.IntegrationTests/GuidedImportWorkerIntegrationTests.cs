using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Exercises normal, interrupted and incomplete imports against the real job store and catalog publisher.</summary>
    /// <remarks>2026-11-24: Bind worker recovery for FR-BEETS-001#ac-import and ac-recover, TEST-BEETS-002. Supplying BEETSMAN_TEST_BEETS and BEETSMAN_TEST_FFMPEG binds the same cases to native processes.</remarks>
    /// <remarks>2026-11-26: Reuse the recovery scenarios for the named FR-BEETS-001#ac-recover case.</remarks>
    internal async Task GuidedImportWorkerReconcilesBeforeAnyRepeatWrite(string scenario)
    {
        var database = Path.Combine(root, "worker-host.db");
        var nativeCatalog = Path.Combine(root, "native.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var stageRoot = Path.Combine(root, "stage");
        var workRoot = Path.Combine(root, "work");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        SeedHost(database);
        using (var db = new LiteDatabase(database))
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 41L });
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var names = new[] { "North", "South" };
        var paths = names.Select(name => Path.Combine(source, name + ".flac")).ToArray();
        var bytes = new byte[paths.Length][];
        for (var index = 0; index < paths.Length; index++)
        {
            if (ffmpeg is null)
                await File.WriteAllTextAsync(paths[index], "fixture " + names[index], Token);
            else
                await GenerateAudioAsync(ffmpeg, paths[index], names[index], index + 1, Token);
            bytes[index] = await File.ReadAllBytesAsync(paths[index], Token);
        }
        var identity = new AuthenticatedHostInstance(library, instance);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var manifest = new BeginImportUploadRequest(instance, Guid.NewGuid(), [.. paths.Select((path, index) =>
            new ImportUploadFile(Path.GetFileName(path), bytes[index].LongLength, Convert.ToHexString(SHA256.HashData(bytes[index]))))]);
        var native = new ScriptedNativeImport(music, bytes);
        Guid jobId;
        string? altered = null;
        byte[]? correctAudio = null;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var intake = host.Services.GetRequiredService<HostImportIntake>();
            Require(await intake.BeginAsync(library, identity, manifest, Token));
            for (var index = 0; index < paths.Length; index++)
            {
                using var body = new MemoryStream(bytes[index]);
                Require(await intake.PutPartAsync(library, identity, manifest.UploadId, index, bytes[index].LongLength, body, _ => { }, Token));
            }
            jobId = Require(await intake.StartAsync(library, identity, new(instance, Guid.NewGuid(), manifest.UploadId), Token)).JobId;
            if (scenario == "Hosted")
            {
                var available = await WaitForPhase(intake, jobId, ImportJobPhase.ReviewRequired);
                var album = Assert.IsType<ImportAlbumReview>(available.Review);
                Require(await host.Services.GetRequiredService<HostImportReview>().ChooseAsync(library, jobId, identity,
                    new(instance, Guid.NewGuid(), album.ItemId, available.Revision, ImportChoiceKind.KeepTags), Token));
                Assert.Equal(42, (await WaitForPhase(intake, jobId, ImportJobPhase.Published)).CatalogRevision);
                return;
            }
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var recording = BindWorker(host, out var worker, scenario == "First" ? null : interrupted.Cancel);
            Assert.True(Require(await worker.RunNextAsync(Token)));
            var offered = Require(await intake.ReadJobAsync(library, identity, jobId, Token));
            var review = Assert.IsType<ImportAlbumReview>(offered.Review);
            Assert.Empty(review.Matches);
            Require(await host.Services.GetRequiredService<HostImportReview>().ChooseAsync(library, jobId, identity,
                new(instance, Guid.NewGuid(), review.ItemId, offered.Revision, ImportChoiceKind.KeepTags), Token));
            if (scenario == "First")
            {
                Assert.True(Require(await worker.RunNextAsync(Token)));
                ImportWorkerAssertions.FirstRun(await Observe(host, recording));
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunNextAsync(interrupted.Token));
                Assert.True(recording.AttemptWasRecorded);
                Assert.Equal(["read", "write"], recording.Calls);
                Assert.Equal(ImportJobPhase.Running, Require(await intake.ReadJobAsync(library, identity, jobId, Token)).Phase);
                if (scenario == "Incomplete")
                {
                    altered = Directory.EnumerateFiles(music, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
                    correctAudio = await File.ReadAllBytesAsync(altered, Token);
                    await File.WriteAllTextAsync(altered, "different bytes in the expected path", Token);
                }
            }
        }

        if (scenario == "First")
            return;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var recording = BindWorker(host, out var worker);
            Assert.True(Require(await worker.RunNextAsync(Token)));
            var observed = await Observe(host, recording);
            if (scenario == "Recovered")
            {
                ImportWorkerAssertions.Recovered(observed);
                return;
            }
            ImportWorkerAssertions.Uncertain(observed);
            await File.WriteAllBytesAsync(altered!, correctAudio!, Token);
            using var wire = new LostResponseHandler(CreateClientHandler(certificate));
            using var http = new HttpClient(wire);
            var gateway = new HttpImportIntakeHost(http, new Uri(host.Urls.Single()), library, new TokenProvider(library, instance, secret));
            var routes = new RequestDispatcher.Builder().RegisterQuery<GetHostLibraryState, HostLibraryState>(new AvailableImportLibrary(library));
            var dispatcher = new ImportIntakeHandlers(library, new ImportFilePicker(), gateway).Register(routes).Build();
            using var viewModel = new HostLibraryViewModel(new(dispatcher, new InlineScheduler(), instance), Guid.NewGuid(), library);
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenLibraryCommand.ExecuteAsync(null);
            await viewModel.InspectJobCommand.ExecuteAsync(jobId);
            Assert.True(viewModel.Job?.Retryable);
            wire.DropNextSuccess = true;
            await viewModel.RetryJobCommand.ExecuteAsync(null);
            Assert.Null(viewModel.AcceptedJob);
            Assert.True(viewModel.Failure?.CanRetry);
            await viewModel.RetryJobCommand.ExecuteAsync(null);
            Assert.Equal(jobId, viewModel.AcceptedJob?.JobId);
            Assert.True(Require(await worker.RunNextAsync(Token)));
            recording.Calls.RemoveAt(0);
            ImportWorkerAssertions.Recovered(await Observe(host, recording));
        }

        RecordingBeetsImport BindWorker(WebApplication host, out GuidedImportWorker worker, Action? afterWrite = null)
        {
            var stage = host.Services.GetRequiredService<FileImportStage>();
            var store = host.Services.GetRequiredService<LiteDbHostPlaylistStore>();
            IBeetsGuidedImport backend = beets is null ? native : new BeetsGuidedImport(new(beets, nativeCatalog, CreateCatalogIfMissing: true), music, workRoot, stage);
            var recording = new RecordingBeetsImport(backend, store, afterWrite);
            worker = new(library, store, host.Services.GetRequiredService<HostImportReview>(), host.Services.GetRequiredService<ImportCatalogPublication>(), recording);
            return recording;
        }

        async Task<ImportWorkerObservation> Observe(WebApplication host, RecordingBeetsImport recording)
        {
            var job = Require(await host.Services.GetRequiredService<HostImportIntake>().ReadJobAsync(library, identity, jobId, Token));
            var original = await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path, Token)));
            var stagePaths = Directory.GetFiles(stageRoot, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var copies = await Task.WhenAll(stagePaths.Select(path => File.ReadAllBytesAsync(path, Token)));
            var retained = copies.Length == paths.Length && original.Zip(bytes).All(pair => pair.First.SequenceEqual(pair.Second)) &&
                copies.Zip(bytes).All(pair => pair.First.SequenceEqual(pair.Second));
            return new([.. recording.Calls], recording.AttemptWasRecorded, recording.Calls.Count(call => call == "write"),
                job.Phase.ToString(), job.FailureCode, job.Retryable, job.CatalogRevision, retained);
        }

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            var options = new LibraryHostOptions(library, database, music, stageRoot, workRoot, []);
            if (scenario == "Hosted")
            {
                options = options with { Beets = new(beets ?? Path.Combine(root, "scripted-beets"), nativeCatalog, CreateCatalogIfMissing: true) };
                if (beets is null)
                    builder.Services.AddSingleton<IBeetsGuidedImport>(native);
            }
            return LibraryHostStartup.Build(builder, options,
                new Verifier(identity, secret, null), credentials, credentials);
        }

        async Task<ImportJobSnapshot> WaitForPhase(HostImportIntake host, Guid job, ImportJobPhase expected)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var state = Require(await host.ReadJobAsync(library, identity, job, deadline.Token));
                if (state.Phase == expected || state.Phase == ImportJobPhase.Failed)
                {
                    Assert.True(state.Phase == expected, $"Expected {expected}, observed {state.Phase} ({state.FailureCode}).");
                    return state;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
        }
    }

    private static T Require<T>(HostOutcome<T> value) => Assert.IsType<HostOutcome<T>.Success>(value).Value;

    private static async Task GenerateAudioAsync(string ffmpeg, string path, string title, int number, CancellationToken token)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        var tone = FormattableString.Invariant($"sine=frequency={500 + 100 * number}:sample_rate=44100");
        foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", tone, "-t", "0.1",
            "-metadata", "album=Fixture Album", "-metadata", "artist=Fixture Band", "-metadata", "album_artist=Fixture Band",
            "-metadata", "title=" + title, "-metadata", $"track={number}", "-c:a", "flac", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await errors);
    }

    /// <summary>Records journal state and a controlled interruption after a native write but before readback.</summary>
    private sealed class RecordingBeetsImport(IBeetsGuidedImport inner, IGuidedImportWorkStore store, Action? afterWrite) : IBeetsGuidedImport
    {
        public List<string> Calls { get; } = [];
        public bool AttemptWasRecorded { get; private set; }

        public Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token) => inner.DiscoverAsync(upload, token);

        public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) => inner.PrepareAsync(work, token);

        public Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token)
        {
            Calls.Add("read");
            return inner.ObserveAsync(work, token);
        }

        public async Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token)
        {
            Calls.Add("write");
            AttemptWasRecorded = Require(await store.ReadNextImportAsync(work.Job.LibraryId, token))?.NativeAttempted == true;
            var result = await inner.ApplyAsync(work, token);
            afterWrite?.Invoke();
            return result;
        }
    }

    /// <summary>Emulates an external catalog in the deterministic integration lane while preserving file effects.</summary>
    private sealed class ScriptedNativeImport(string music, byte[][] bytes) : IBeetsGuidedImport
    {
        public Task<HostOutcome<ImmutableArray<BeetsImportCandidate>>> DiscoverAsync(ImportUpload upload, CancellationToken token) =>
            Task.FromResult<HostOutcome<ImmutableArray<BeetsImportCandidate>>>(new HostOutcome<ImmutableArray<BeetsImportCandidate>>.Success([]));

        public Task<HostOutcome<PreparedImportAlbum?>> PrepareAsync(GuidedImportWork work, CancellationToken token) =>
            Task.FromResult<HostOutcome<PreparedImportAlbum?>>(new HostOutcome<PreparedImportAlbum?>.Success(null));

        public async Task<HostOutcome<bool>> ApplyAsync(GuidedImportWork work, CancellationToken token)
        {
            for (var index = 0; index < bytes.Length; index++)
                await File.WriteAllBytesAsync(Path.Combine(music, PathFor(index)), bytes[index], token);
            return new HostOutcome<bool>.Success(true);
        }

        public async Task<HostOutcome<NativeImportObservation>> ObserveAsync(GuidedImportWork work, CancellationToken token)
        {
            NativeImportObservation observation = new NativeImportObservation.Absent();
            if (File.Exists(Path.Combine(music, PathFor(0))))
            {
                for (var index = 0; index < bytes.Length; index++)
                {
                    var content = await File.ReadAllBytesAsync(Path.Combine(music, PathFor(index)), token);
                    if (!content.SequenceEqual(bytes[index]))
                        return new HostOutcome<NativeImportObservation>.Success(new NativeImportObservation.Incomplete());
                }
                observation = new NativeImportObservation.Complete(new(work.Job.LibraryId, work.Job.JobId, work.Upload.UploadId,
                    work.ChoiceOperationId!.Value, work.Job.Revision, null, 1, "Fixture Album", "Fixture Band",
                    [.. Enumerable.Range(0, bytes.Length).Select(index => new ObservedImportTrack(index, index + 1, PathFor(index),
                        $"Track {index + 1}", "Fixture Band", index + 1, null, null))]));
            }
            return new HostOutcome<NativeImportObservation>.Success(observation);
        }

        private static string PathFor(int index) => $"Track_{index + 1}.flac";
    }
}
