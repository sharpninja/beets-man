using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
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
    /// <summary>Recovers an interrupted mixed native batch and publishes one peer-visible revision.</summary>
    /// <remarks>2026-11-24: Bind TEST-LIBRARY-003/004 for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
    /// <remarks>2026-11-26: Allow native beets operations to finish after host restart and report the last state on timeout for TEST-LIBRARY-004 and FR-BEETS-001#ac-recover.</remarks>
    [Fact]
    public async Task MixedNativeBatchResumesBeforePublishingAnyCatalogEffect()
    {
        const string peerToken = "mixed-native-peer";
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var hostDb = Path.Combine(root, "mixed.db");
        var peerDb = Path.Combine(root, "peer.db");
        var stage = Path.Combine(root, "private");
        var work = Path.Combine(root, "work");
        var nativeDb = Path.Combine(config, "library.db");
        var settings = Path.Combine(config, "config.yaml");
        var harborFile = Path.Combine(music, "Harbor.flac");
        var removedFile = Path.Combine(music, "Removed.flac");
        var nativeIds = new long[] { 27, 28 };
        if (beets is not null)
        {
            await WriteAudio(harborFile, "Harbor", 440);
            await WriteAudio(removedFile, "Removed", 660);
            await File.WriteAllTextAsync(settings, new SerializerBuilder().Build().Serialize(new Dictionary<string, object>
                { ["library"] = nativeDb, ["directory"] = music, ["plugins"] = Array.Empty<string>() }), Token);
            await Run(beets, ["-c", settings, "import", "-C", "-M", "-W", "-P", "-I", "-A", "-s", "-q", "--", harborFile, removedFile]);
            nativeIds = [await NativeId("Harbor"), await NativeId("Removed")];
        }
        else
        {
            await File.WriteAllTextAsync(harborFile, "Harbor audio", Token);
            await File.WriteAllTextAsync(removedFile, "Removed audio", Token);
        }
        var removedBytes = await File.ReadAllBytesAsync(removedFile, Token);
        var harbor = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var saved = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var scripted = new MixedNativeFixture(nativeIds);
        var tagWrites = 0;
        var removeWrites = 0;
        var interruption = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SeedHost(hostDb);
        using (var database = new LiteDatabase(hostDb))
        {
            database.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            database.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 42L });
            var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(Track(harbor, "Harbor", "Harbor.flac", nativeIds[0]));
            tracks.Insert(Track(removed, "Removed", "Removed.flac", nativeIds[1]));
            tracks.Insert(Track(stale, "Stale", "Stale.flac", nativeIds[1]));
            database.GetCollection<BsonDocument>("saved_playlists").Insert(new BsonDocument
                { ["_id"] = saved, ["LibraryId"] = library, ["Revision"] = 1L, ["Name"] = "Before", ["Entries"] = new BsonArray() });
        }
        using (LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: true)) { }
        using (var database = new LiteDatabase(peerDb))
        {
            database.GetCollection<BsonDocument>("sync_checkpoints").Insert(new BsonDocument
                { ["_id"] = $"{library:N}/catalog", ["LibraryId"] = library, ["Feed"] = "catalog", ["AppliedRevision"] = 42L });
            database.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(removed, "Removed", "Removed.flac", nativeIds[1]));
        }

        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance> { [secret] = new(library, instance), [peerToken] = new(library, peer) });
        var options = new BeetsImportOptions(beets ?? Environment.ProcessPath!, nativeDb, CreateCatalogIfMissing: true);
        ApproveLibraryBatchRequest approved;
        ApprovedLibraryBatchStatus running;
        long interruptedFeed;
        string interruptedGenre;
        bool blocked;
        await using (var host = Host(interruption))
        {
            await host.StartAsync(Token);
            using var client = Client(host, secret);
            approved = await Approval(client, removed, 42, 1);
            await Submit(client, approved);
            await interruption.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            running = await Status(client, approved.OperationId, Token);
            interruptedFeed = (await Feed(client, 42)).ThroughRevision;
            interruptedGenre = await Genre();
            using var outside = await client.PostAsJsonAsync(PlaylistPublicationRoute.Path(library), new PlaylistSaveRequest(instance, Guid.NewGuid(),
                new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), saved, 1, "Competing", [new(Guid.NewGuid(), harbor)]), Token);
            blocked = outside.StatusCode == HttpStatusCode.Conflict;
            await host.StopAsync(Token);
        }
        bool original;
        using (var database = new LiteDatabase(hostDb))
            original = database.GetCollection<BsonDocument>("catalog_tracks").FindById(harbor)["Genre"].AsString == "Ambient" &&
                database.GetCollection<BsonDocument>("saved_playlists").FindById(saved)["Name"].AsString == "Before" &&
                database.GetCollection<BsonDocument>("revision_changes").Count() == 0;

        await using (var host = Host(null))
        {
            await host.StartAsync(Token);
            using var client = Client(host, secret);
            using var peerClient = Client(host, peerToken);
            var published = await Terminal(client, approved.OperationId);
            var feed = await Feed(client, 42);
            var retry = await Submit(client, approved);
            var rejectedApproval = await Approval(client, stale, 43, 2);
            await Submit(client, rejectedApproval);
            var rejected = await Terminal(client, rejectedApproval.OperationId);
            var afterRetry = await Feed(client, 43);
            bool missing = beets is null ? scripted.Removed : string.IsNullOrWhiteSpace(await Run(beets, ["-c", settings, "list", "title:=Removed"]));
            var currentBytes = await File.ReadAllBytesAsync(removedFile, Token);
            var retained = removedBytes.SequenceEqual(currentBytes);
            using var replicaDb = LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: false);
            var replica = new CatalogReplicaHandlers(library, peer, new HttpCatalogReplicaHost(peerClient, peerClient.BaseAddress!, library, peer,
                new TokenProvider(library, peer, peerToken)), replicaDb);
            var outcome = await replica.HandleAsync(new ResumeReplication(library, peer, ReplicaRecoveryMode.Incremental, 42),
                new(new(Guid.NewGuid(), peer), Guid.NewGuid()), Token);
            var replicated = Assert.IsType<Outcome<ReplicationState>.Success>(outcome).Value;
            var reader = new LiteDbCatalogReader(replicaDb, library, music, () => false);
            var read = new RequestContext(Guid.NewGuid(), peer);
            var page = Assert.IsType<Outcome<CatalogPage>.Success>(await reader.HandleAsync(
                new BrowseCatalog(CatalogCategory.Tracks, new(), new(CatalogSortKey.Title)), read, Token)).Value;
            var playlist = Success(await reader.ReadAsync(saved, Token));
            var peerTracks = page.Rows.Select(row => row.Title).ToArray();
            var trackTitles = page.Rows.ToDictionary(row => row.Id, row => row.Title);
            var peerPlaylist = playlist.Entries.Select(entry => trackTitles[entry.TrackId]).ToArray();
            var revision = Assert.Single(feed.Revisions);
            Assert.Equal(approved.OperationId, revision.OperationId);
            MixedNativeBatchAssertions.Verify(new(running.Phase.ToString(), interruptedFeed, interruptedGenre, original, blocked,
                published.Phase.ToString(), feed.ThroughRevision, await Genre(), missing && retained, tagWrites, removeWrites, feed.Revisions.Length,
                [.. revision.Changes.Select(change => $"{change.Kind}:{change.Operation}")], peerTracks, peerPlaylist, replicated.Cursor,
                retry.OperationId == approved.OperationId && retry.CatalogRevision == published.CatalogRevision && retry.Playlists.SequenceEqual(published.Playlists),
                afterRetry.Revisions.IsEmpty, rejected.Phase.ToString() == "Rejected" && rejected.CatalogRevision is null));
        }

        static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

        BsonDocument Track(Guid id, string title, string path, long nativeId) => new() { ["_id"] = id, ["LibraryId"] = library, ["Revision"] = 42L,
            ["BeetsTrackId"] = nativeId, ["Title"] = title, ["Genre"] = "Ambient", ["RelativePath"] = path };
        async Task<ApproveLibraryBatchRequest> Approval(HttpClient client, Guid removal, long head, long playlistRevision)
        {
            using var response = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.PreviewPath(library),
                new LibraryMaintenancePreviewRequest(LibraryMaintenanceKind.RemoveCatalog, head, [removal]), Token);
            response.EnsureSuccessStatusCode();
            var plan = (await response.Content.ReadFromJsonAsync<LibraryMaintenancePlan>(Token))!;
            var entries = ImmutableArray.CreateBuilder<ApprovedBatchEntry>();
            // A valid tag edit before the stale removal proves that rejection preflights the whole review.
            var before = head == 42 ? "Ambient" : "Downtempo";
            var after = head == 42 ? "Downtempo" : "Jazz";
            entries.Add(new(Guid.NewGuid(), 1, new ApprovedBatchChange.TrackTags(harbor, head,
                ImmutableSortedDictionary<string, string>.Empty.Add("title", "Harbor").Add("genre", before),
                ImmutableSortedDictionary<string, string?>.Empty.Add("genre", after))));
            entries.Add(new(Guid.NewGuid(), entries.Count + 1, new ApprovedBatchChange.Maintenance(plan, false)));
            entries.Add(new(Guid.NewGuid(), entries.Count + 1, new ApprovedBatchChange.Playlist(new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()),
                1, saved, playlistRevision, "Reviewed", [new(Guid.NewGuid(), harbor), new(Guid.NewGuid(), harbor)])));
            return new(instance, Guid.NewGuid(), Guid.NewGuid(), 4, Guid.NewGuid(), entries.ToImmutable());
        }
        async Task<ApprovedLibraryBatchStatus> Submit(HttpClient client, ApproveLibraryBatchRequest request)
        {
            using var response = await client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), request, Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            return (await response.Content.ReadFromJsonAsync<ApprovedLibraryBatchStatus>(Token))!;
        }
        async Task<CatalogFeedPage> Feed(HttpClient client, long after) => (await client.GetFromJsonAsync<CatalogFeedPage>(
            CatalogReplicationRoutes.ChangesPath(library, after, null, 10), Token))!;
        async Task<ApprovedLibraryBatchStatus> Status(HttpClient client, Guid operation, CancellationToken token) =>
            (await client.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, operation), token))!;
        async Task<ApprovedLibraryBatchStatus> Terminal(HttpClient client, Guid operation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(beets is null ? 20 : 60));
            ApprovedLibraryBatchStatus? status = null;
            try
            {
                while (true)
                {
                    status = await Status(client, operation, timeout.Token);
                    if (status.Phase is BatchPublicationPhase.Published or BatchPublicationPhase.Rejected)
                        return status;
                    await Task.Delay(100, timeout.Token);
                }
            }
            catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !Token.IsCancellationRequested)
            {
                throw new TimeoutException($"Batch {operation} did not publish or reject. Last phase: {status?.Phase}; failure: {status?.FailureCode}.", error);
            }
        }
        WebApplication Host(TaskCompletionSource? pause)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            var native = new BeetsGuidedImport(options, music, work, new FileImportStage(stage));
            builder.Services.AddSingleton<IBeetsTrackTags>(new TagExecutionTransport(beets is null ? scripted : native, () => Interlocked.Increment(ref tagWrites), pause));
            builder.Services.AddSingleton<IBeetsCatalogRemoval>(new CountingMixedRemoval(beets is null ? scripted : native, () => Interlocked.Increment(ref removeWrites)));
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, stage, work, [], Beets: options), verifier, credentials, credentials);
        }
        HttpClient Client(WebApplication host, string credential)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return client;
        }
        Task<string> WriteAudio(string file, string title, int hz) => Run(ffmpeg!, ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency={hz}:duration=0.1",
            "-c:a", "flac", "-metadata", $"title={title}", "-metadata", "genre=Ambient", file]);
        async Task<long> NativeId(string title) => long.Parse((await Run(beets!, ["-c", settings, "list", "-f", "${id}", $"title:={title}"])).Trim(), CultureInfo.InvariantCulture);
        async Task<string> Genre() => ffmpeg is null ? scripted.Genre : Assert.Single((await Run(ffmpeg,
            ["-v", "error", "-i", harborFile, "-f", "ffmetadata", "-"])).Split('\n'), line => line.StartsWith("GENRE=", StringComparison.OrdinalIgnoreCase))[6..].Trim();
        async Task<string> Run(string tool, string[] arguments)
        {
            var command = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            command.Environment["BEETSDIR"] = config;
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

    /// <summary>Records each actual CLI removal attempt in the integration fixture.</summary>
    private sealed class CountingMixedRemoval(IBeetsCatalogRemoval inner, Action count) : IBeetsCatalogRemoval
    {
        public Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token) => inner.ObserveCatalogTrackAsync(track, token);
        public Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            count();
            return inner.RemoveCatalogTrackAsync(track, token);
        }
    }

    /// <summary>Provides deterministic native state for integration runs without the optional CLI tools.</summary>
    private sealed class MixedNativeFixture(long[] ids) : IBeetsTrackTags, IBeetsCatalogRemoval
    {
        public string Genre { get; private set; } = "Ambient";
        public bool Removed { get; private set; }
        public Task<HostOutcome<NativeTrackTags>> ObserveAsync(TrackTagWork work, CancellationToken token)
        {
            Assert.Equal(ids[0], work.BeetsTrackId);
            Assert.Equal("Harbor.flac", work.RelativePath);
            var fields = ImmutableDictionary<string, string>.Empty.Add("genre", Genre).Add("title", "Harbor");
            return Task.FromResult<HostOutcome<NativeTrackTags>>(new HostOutcome<NativeTrackTags>.Success(new(fields, fields)));
        }
        public Task<HostOutcome<bool>> ApplyAsync(TrackTagWork work, CancellationToken token)
        {
            Assert.True(work.NativeAttempted);
            Genre = work.Changes["genre"]!;
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(true));
        }
        public Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            var state = track.RelativePath switch
            {
                "Harbor.flac" when track.BeetsTrackId == ids[0] => NativeCatalogTrackState.Present,
                "Removed.flac" when track.BeetsTrackId == ids[1] => Removed ? NativeCatalogTrackState.Missing : NativeCatalogTrackState.Present,
                _ => NativeCatalogTrackState.Diverged
            };
            return Task.FromResult<HostOutcome<NativeCatalogTrackState>>(new HostOutcome<NativeCatalogTrackState>.Success(state));
        }
        public Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            Assert.False(Removed);
            Removed = true;
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(true));
        }
    }
}
