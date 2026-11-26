using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using YamlDotNet.Serialization;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Interrupts an existing-track tag mutation after its write and verifies native and peer state on restart.</summary>
    /// <remarks>2026-11-24: Bind isolated and native providers for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
    [Fact]
    public async Task NativeTagEditPublishesOnceAfterInterruptionWithVerifiedFileTags()
    {
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var stageRoot = Path.Combine(root, "private");
        var workRoot = Path.Combine(root, "work");
        var configRoot = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var nativeDatabase = Path.Combine(configRoot, "library.db");
        var settings = Path.Combine(configRoot, "config.yaml");
        var database = Path.Combine(root, "tag-execution.db");
        var originalSource = Path.Combine(root, "Original.flac");
        const string relativePath = "Harbor Lights.flac";
        var selectedFile = Path.Combine(music, relativePath);
        var siblingFile = Path.Combine(music, "After Hours.flac");
        var executable = beets ?? (Environment.ProcessPath ?? throw new InvalidOperationException("No process executable."));
        var options = new BeetsImportOptions(executable, nativeDatabase, CreateCatalogIfMissing: true);
        long nativeTrackId = 27;
        string? originalAudio = null;
        if (beets is not null)
        {
            await WriteFixtureAudio(originalSource, "Harbor Lights", "440");
            await WriteFixtureAudio(siblingFile, "After Hours", "660");
            File.Copy(originalSource, selectedFile);
            var config = new Dictionary<string, object> { ["library"] = nativeDatabase, ["directory"] = music, ["plugins"] = Array.Empty<string>() };
            await File.WriteAllTextAsync(settings, new SerializerBuilder().Build().Serialize(config), Token);
            await RunTool(beets, ["-c", settings, "import", "-C", "-M", "-W", "-P", "-I", "-A", "-s", "-q", "--", selectedFile, siblingFile]);
            nativeTrackId = long.Parse((await RunTool(beets, ["-c", settings, "list", "-f", "${id}", "title:Harbor Lights"])).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            originalAudio = await AudioFingerprint(selectedFile);
        }
        else
        {
            await File.WriteAllTextAsync(originalSource, "Original encoded fixture", Token);
            File.Copy(originalSource, selectedFile);
            await File.WriteAllTextAsync(siblingFile, "Other encoded fixture", Token);
        }
        var sourceBytes = await File.ReadAllBytesAsync(originalSource, Token);
        var siblingBytes = await File.ReadAllBytesAsync(siblingFile, Token);
        var track = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var operation = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = recipient, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 42L, ["MinRetainedRevision"] = 0L });
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
            {
                ["_id"] = track, ["LibraryId"] = library, ["BeetsTrackId"] = nativeTrackId, ["Revision"] = 42L,
                ["Title"] = "Harbor Lights", ["RelativePath"] = relativePath, ["Genre"] = "Ambient", ["Year"] = 2023
            });
        }

        var scripted = new ScriptedTagExecution();
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        TrackTagEditReceipt receipt;
        ImportJobSnapshot running;
        await using (var host = BuildHost(stopAfterWrite: true))
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            var patch = ImmutableDictionary<string, string?>.Empty.Add("genre", TrackTagExecutionAssertions.UpdatedGenre).Add("year", null);
            using var response = await client.PostAsJsonAsync(TrackTagRoutes.Path(library, track), new EditTrackTagsRequest(instance, operation, 42, patch), Token);
            response.EnsureSuccessStatusCode();
            receipt = (await response.Content.ReadFromJsonAsync<TrackTagEditReceipt>(Token))!;
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            running = (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, receipt.JobId), Token))!;
            await host.StopAsync(Token);
        }

        string originalGenre;
        long originalRevision;
        using (var db = new LiteDatabase(database))
        {
            originalGenre = db.GetCollection<BsonDocument>("catalog_tracks").FindById(track)["Genre"].AsString;
            originalRevision = db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
            Assert.Empty(db.GetCollection<BsonDocument>("revision_changes").FindAll());
        }

        ImportJobSnapshot published;
        TrackTagsSnapshot tags;
        await using (var host = BuildHost(stopAfterWrite: false))
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            do
            {
                published = (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, receipt.JobId), limit.Token))!;
                if (published.Phase is ImportJobPhase.Published or ImportJobPhase.Failed)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(100), limit.Token);
            } while (true);
            Assert.True(published.Phase == ImportJobPhase.Published, $"Tag edit ended {published.Phase}: {published.FailureCode}.");
            tags = (await client.GetFromJsonAsync<TrackTagsSnapshot>(TrackTagRoutes.Path(library, track), Token))!;
        }

        string embeddedGenre;
        string? embeddedYear;
        bool audioRetained;
        if (ffmpeg is null)
        {
            embeddedGenre = scripted.Current["genre"];
            embeddedYear = scripted.Current.GetValueOrDefault("year");
            var selectedBytes = await File.ReadAllBytesAsync(selectedFile, Token);
            audioRetained = sourceBytes.SequenceEqual(selectedBytes);
        }
        else
        {
            var exported = Path.Combine(root, "readback.ffmetadata");
            await RunTool(ffmpeg, ["-v", "error", "-i", selectedFile, "-f", "ffmetadata", exported]);
            var metadata = await File.ReadAllLinesAsync(exported, Token);
            embeddedGenre = Assert.Single(metadata, line => line.StartsWith("GENRE=", StringComparison.OrdinalIgnoreCase))[6..];
            embeddedYear = metadata.SingleOrDefault(line => line.StartsWith("DATE=", StringComparison.OrdinalIgnoreCase))?[5..];
            audioRetained = originalAudio == await AudioFingerprint(selectedFile);
        }

        var retainedSource = await File.ReadAllBytesAsync(originalSource, Token);
        var retainedSibling = await File.ReadAllBytesAsync(siblingFile, Token);
        using (var db = new LiteDatabase(database))
        {
            var revisions = db.GetCollection<BsonDocument>("revision_changes").FindAll().ToArray();
            var revision = Assert.Single(revisions);
            var update = Assert.Single(revision["Changes"].AsArray).AsDocument["Payload"].AsDocument;
            Assert.Equal(track, update["_id"].AsGuid);
            Assert.Equal(tags.CatalogRevision, update["Revision"].AsInt64);
            Assert.False(tags.Tags.ContainsKey("year"));
            var notified = db.GetCollection<BsonDocument>("notification_deliveries").FindAll().Select(row => row["RecipientInstanceId"].AsGuid).ToArray();
            TrackTagExecutionAssertions.Verify(new(receipt.JobId, published.JobId, operation, revision["OperationId"].AsGuid, originalGenre,
                tags.Tags["genre"], embeddedGenre, embeddedYear, update["RelativePath"].AsString == relativePath && File.Exists(selectedFile),
                sourceBytes.SequenceEqual(retainedSource), siblingBytes.SequenceEqual(retainedSibling), audioRetained, running.Phase.ToString(), originalRevision,
                published.Phase.ToString(), published.CatalogRevision!.Value, writes, revisions.Length, recipient, notified));
        }

        WebApplication BuildHost(bool stopAfterWrite)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            IBeetsTrackTags adapter = beets is null ? scripted : new BeetsGuidedImport(options, music, workRoot, new FileImportStage(stageRoot));
            builder.Services.AddSingleton<IBeetsTrackTags>(new TagExecutionTransport(adapter, () => Interlocked.Increment(ref writes),
                stopAfterWrite ? interrupted : null));
            return LibraryHostStartup.Build(builder, new(library, database, music, stageRoot, workRoot, [], Beets: options),
                new Verifier(new(library, instance), secret, null), credentials, credentials);
        }

        HttpClient Client(WebApplication host)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return client;
        }

        Task<string> WriteFixtureAudio(string output, string title, string frequency) => RunTool(ffmpeg!,
            ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency={frequency}:duration=0.1", "-c:a", "flac", "-metadata", $"title={title}",
             "-metadata", "genre=Ambient", "-metadata", "date=2023", output]);
        Task<string> AudioFingerprint(string file) => RunTool(ffmpeg!, ["-v", "error", "-i", file, "-map", "0:a:0", "-c", "copy", "-f", "hash", "-"]);

        async Task<string> RunTool(string tool, string[] arguments)
        {
            var command = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            command.Environment["BEETSDIR"] = configRoot;
            foreach (var argument in arguments)
                command.ArgumentList.Add(argument);
            using var process = Process.Start(command)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Token);
            var stderr = process.StandardError.ReadToEndAsync(Token);
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return await stdout;
        }
    }

    /// <summary>Stops the real host immediately after its one native write.</summary>
    private sealed class TagExecutionTransport(IBeetsTrackTags inner, Action write, TaskCompletionSource? interruption) : IBeetsTrackTags
    {
        public Task<HostOutcome<NativeTrackTags>> ObserveAsync(TrackTagWork work, CancellationToken token) => inner.ObserveAsync(work, token);

        public async Task<HostOutcome<bool>> ApplyAsync(TrackTagWork work, CancellationToken token)
        {
            write();
            var result = await inner.ApplyAsync(work, token);
            if (interruption is not null && result is HostOutcome<bool>.Success { Value: true })
            {
                interruption.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return result;
        }
    }

    private sealed class ScriptedTagExecution : IBeetsTrackTags
    {
        public ImmutableDictionary<string, string> Current { get; private set; } =
            ImmutableDictionary<string, string>.Empty.Add("genre", "Ambient").Add("year", "2023");

        public Task<HostOutcome<NativeTrackTags>> ObserveAsync(TrackTagWork work, CancellationToken token) =>
            Task.FromResult<HostOutcome<NativeTrackTags>>(new HostOutcome<NativeTrackTags>.Success(new(Current, Current)));

        public Task<HostOutcome<bool>> ApplyAsync(TrackTagWork work, CancellationToken token)
        {
            Assert.True(work.NativeAttempted);
            Assert.Equal("Harbor Lights.flac", work.RelativePath);
            Current = Current.Remove("year").SetItem("genre", "Downtempo $title 100%");
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(true));
        }
    }
}
