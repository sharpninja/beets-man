using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
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
    /// <summary>Copies approved media, restarts after native repointing, and publishes the exact path once.</summary>
    /// <remarks>2026-11-24: Bind TEST-BEETS-002 for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
    /// <remarks>2026-11-26: Allow native settlement time and stop the resumed host before cleanup for TEST-BEETS-002 and FR-LIBRARY-002#ac-batch-commit.</remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApprovedOrganizationRecoversVerifiedCopyAndRefusesLateCollision(bool batch, bool removeAudio)
    {
        const string source = OrganizationExecutionAssertions.Source;
        const string destination = OrganizationExecutionAssertions.Destination;
        const string otherSource = "Inbox/Other.flac";
        const string collision = "Lena Vale/Northern Lines/02 Night Crossing.flac";
        const string peerToken = "organization-peer-fixture";
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var inbox = Directory.CreateDirectory(Music("Inbox")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "config")).FullName;
        var nativeDb = Path.Combine(config, "library.db");
        var settings = Path.Combine(config, "config.yaml");
        var stage = Path.Combine(root, "private");
        var work = Path.Combine(root, "work");
        var hostDb = Path.Combine(root, "organize.db");
        var peerDb = Path.Combine(root, "organize-peer.db");
        const string template = "$artist/$album/$track $title";
        long harborNative = 27, nightNative = 28;
        if (beets is not null)
        {
            await AudioAsync(Music(source), "Harbor Lights", "1");
            await AudioAsync(Music(otherSource), "Night Crossing", "2");
            await File.WriteAllTextAsync(settings, new SerializerBuilder().Build().Serialize(new Dictionary<string, object>
                { ["library"] = nativeDb, ["directory"] = music, ["plugins"] = Array.Empty<string>() }), Token);
            await NativeAsync(beets, ["-c", settings, "import", "-C", "-M", "-W", "-P", "-I", "-A", "-q", "--flat", "--", inbox]);
            harborNative = await Id("Harbor Lights");
            nightNative = await Id("Night Crossing");
        }
        else
        {
            await File.WriteAllTextAsync(Music(source), "Harbor source fixture", Token);
            await File.WriteAllTextAsync(Music(otherSource), "Night Crossing source fixture", Token);
        }
        var sourceBytes = await File.ReadAllBytesAsync(Music(source), Token);
        var otherBytes = await File.ReadAllBytesAsync(Music(otherSource), Token);
        var harbor = Guid.NewGuid();
        var night = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var scripted = new ScriptedOrganization(music, source, destination, otherSource, collision);
        var observedEdits = new OrganizationBatchEdits(scripted, source, otherSource, harbor, music);
        var audioDeletes = new List<string>();
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance> { [secret] = new(library, instance), [peerToken] = new(library, peer) });
        var options = new BeetsImportOptions(beets ?? Environment.ProcessPath!, nativeDb, template, CreateCatalogIfMissing: true);
        var attempts = 0;
        SeedHost(hostDb);
        using (var db = new LiteDatabase(hostDb))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 42L });
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(harbor, "Harbor Lights", harborNative, source));
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(night, "Night Crossing", nightNative, otherSource));
        }
        using (LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: true)) { }
        using (var db = new LiteDatabase(peerDb))
        {
            db.GetCollection<BsonDocument>("sync_checkpoints").Insert(new BsonDocument
                { ["_id"] = $"{library:N}/catalog", ["LibraryId"] = library, ["Feed"] = "catalog", ["AppliedRevision"] = 42L });
            if (batch)
                db.GetCollection<BsonDocument>("catalog_tracks").Insert(Track(night, "Night Crossing", nightNative, otherSource));
        }

        if (batch)
        {
            await RunBatchAsync();
            return;
        }

        var command = new ConfirmLibraryMaintenanceRequest(instance, Guid.NewGuid(), LibraryMaintenanceKind.Organize, 42);
        LibraryMaintenancePlan preview;
        LibraryMaintenanceReceipt receipt;
        ImportJobSnapshot running;
        bool previewReadOnly;
        bool emptyDuring;
        string nativeDuring;
        await using (var host = Host(interrupted))
        {
            await host.StartAsync(Token);
            using var client = Client(host, secret);
            preview = await Preview(client, harbor, 42);
            previewReadOnly = !File.Exists(Music(destination)) && await NativePath(harborNative, source) == source && (await Feed(client, 42)).Revisions.IsEmpty;
            receipt = await Approve(client, preview.PlanId, command);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            running = await Job(client, receipt.JobId, Token);
            nativeDuring = await NativePath(harborNative, source);
            emptyDuring = (await Feed(client, 42)).Revisions.IsEmpty;
            await host.StopAsync(Token);
        }
        using (var db = new LiteDatabase(hostDb))
            emptyDuring &= db.GetCollection<BsonDocument>("catalog_tracks").FindById(harbor)["RelativePath"].AsString == source;

        ApprovedMoveObservation result;
        await using (var host = Host(null))
        {
            await host.StartAsync(Token);
            using var client = Client(host, secret);
            using var peerClient = Client(host, peerToken);
            var finished = await Terminal(client, receipt.JobId);
            var replay = await Approve(client, preview.PlanId, command);
            var page = await Feed(client, 42);
            var revision = Assert.Single(page.Revisions);
            var update = Assert.Single(revision.Changes);
            Assert.Equal(command.OperationId, revision.OperationId);
            Assert.Equal(harbor, update.RecordId);
            Assert.Equal(destination, Assert.IsType<ReplicaTrack>(update.Entity).RelativePath);
            using (var desktop = LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: false))
            {
                var handler = new CatalogReplicaHandlers(library, peer, new HttpCatalogReplicaHost(peerClient, peerClient.BaseAddress!, library, peer,
                    new TokenProvider(library, peer, peerToken)), desktop);
                var resumed = await handler.HandleAsync(new ResumeReplication(library, peer, ReplicaRecoveryMode.Incremental, 42),
                    new(new(Guid.NewGuid(), peer), Guid.NewGuid()), Token);
                Assert.Equal(43L, Assert.IsType<Outcome<ReplicationState>.Success>(resumed).Value.Cursor);
            }
            var second = await Preview(client, night, 43);
            Assert.Equal(collision, Assert.Single(second.Moves).Destination);
            await File.WriteAllTextAsync(Music(collision), "foreign destination", Token);
            var blocked = await Approve(client, second.PlanId, command with { OperationId = Guid.NewGuid(), ExpectedCatalogRevision = 43 });
            var refused = await Terminal(client, blocked.JobId);
            var after = await Feed(client, 43);
            result = new(finished, replay == receipt, page.Revisions.Length, refused.Phase == ImportJobPhase.Failed &&
                refused.FailureCode == "organization-destination-occupied" && after.Revisions.IsEmpty && await NativePath(nightNative, otherSource) == otherSource,
                await File.ReadAllTextAsync(Music(collision), Token) == "foreign destination");
        }

        var before = Assert.Single(preview.Moves);
        var originalNow = await File.ReadAllBytesAsync(Music(source), Token);
        var otherNow = await File.ReadAllBytesAsync(Music(otherSource), Token);
        var copyNow = await File.ReadAllBytesAsync(Music(destination), Token);
        using var peerStore = new LiteDatabase(peerDb);
        var peerPath = peerStore.GetCollection<BsonDocument>("catalog_tracks").FindById(harbor)["RelativePath"].AsString;
        OrganizationExecutionAssertions.Verify(new(before.Source, before.Destination, previewReadOnly, running.Phase.ToString(), nativeDuring,
            sourceBytes.SequenceEqual(originalNow) && otherBytes.SequenceEqual(otherNow), sourceBytes.SequenceEqual(copyNow), emptyDuring,
            result.Job.Phase.ToString(), result.Job.CatalogRevision!.Value, peerPath, attempts, result.Revisions, result.Replay,
            result.Rejected, result.ForeignRetained, Directory.GetFiles(Path.GetDirectoryName(Music(collision))!, "*.flac").Length == 2));

        string Music(string path) => Path.Combine(music, path.Replace('/', Path.DirectorySeparatorChar));
        BsonDocument Track(Guid id, string title, long nativeId, string path) => new() { ["_id"] = id, ["LibraryId"] = library,
            ["Revision"] = 42L, ["Title"] = title, ["Genre"] = "Ambient", ["BeetsTrackId"] = nativeId, ["RelativePath"] = path };
        async Task<LibraryMaintenancePlan> Preview(HttpClient client, Guid track, long revision)
            => await PreviewSelection(client, LibraryMaintenanceKind.Organize, revision, [track]);

        async Task<LibraryMaintenancePlan> PreviewSelection(HttpClient client, LibraryMaintenanceKind kind, long revision, ImmutableArray<Guid> picks)
        {
            using var response = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.PreviewPath(library),
                new LibraryMaintenancePreviewRequest(kind, revision, picks), Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            return (await response.Content.ReadFromJsonAsync<LibraryMaintenancePlan>(Token))!;
        }
        async Task<LibraryMaintenanceReceipt> Approve(HttpClient client, Guid plan, ConfirmLibraryMaintenanceRequest request)
        {
            using var response = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, plan), request, Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            return (await response.Content.ReadFromJsonAsync<LibraryMaintenanceReceipt>(Token))!;
        }
        async Task<CatalogFeedPage> Feed(HttpClient client, long after) => (await client.GetFromJsonAsync<CatalogFeedPage>(
            CatalogReplicationRoutes.ChangesPath(library, after, null, 10), Token))!;
        async Task<ImportJobSnapshot> Job(HttpClient client, Guid id, CancellationToken ct) =>
            (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, id), ct))!;
        async Task<ImportJobSnapshot> Terminal(HttpClient client, Guid id)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var job = await Job(client, id, limit.Token);
                if (job.Phase is ImportJobPhase.Published or ImportJobPhase.Failed)
                    return job;
                await Task.Delay(100, limit.Token);
            }
        }
        WebApplication Host(TaskCompletionSource? pause)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            if (beets is null)
                builder.Services.AddSingleton<IBeetsMaintenancePlanner>(scripted);
            var native = beets is null ? null : new BeetsGuidedImport(options, music, work, new FileImportStage(stage));
            IBeetsOrganization adapter = native is null ? scripted : native;
            builder.Services.AddSingleton<IBeetsOrganization>(new InterruptOrganization(adapter, () => Interlocked.Increment(ref attempts), pause));
            if (batch)
            {
                observedEdits.UseNative(native);
                builder.Services.AddSingleton<IBeetsTrackTags>(observedEdits);
                builder.Services.AddSingleton<IBeetsCatalogRemoval>(observedEdits);
                if (removeAudio)
                {
                    IAudioRemovalFiles files = native is null ? new ScriptedAudioRemoval(music) : native;
                    builder.Services.AddSingleton<IAudioRemovalFiles>(new CountAudioRemoval(files, audioDeletes));
                }
            }
            return LibraryHostStartup.Build(builder, new(library, hostDb, music, stage, work, [], Beets: options), verifier, credentials, credentials);
        }
        HttpClient Client(WebApplication host, string token)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
        Task<string> AudioAsync(string file, string title, string number) => NativeAsync(ffmpeg!, ["-v", "error", "-f", "lavfi", "-i",
            "sine=frequency=440:duration=0.1", "-c:a", "flac", "-metadata", $"title={title}", "-metadata", "artist=Lena Vale",
            "-metadata", "album_artist=Lena Vale", "-metadata", "album=Northern Lines", "-metadata", "genre=Ambient", "-metadata", $"track={number}", file]);
        async Task<long> Id(string title) => long.Parse((await NativeAsync(beets!, ["-c", settings, "list", "-f", "${id}", $"title:={title}"])).Trim(), CultureInfo.InvariantCulture);
        async Task<string> NativePath(long id, string original) => beets is null ? scripted.PathFor(original) : Path.GetRelativePath(music,
            (await NativeAsync(beets, ["-c", settings, "list", "-f", "${path}", $"id:{id.ToString(CultureInfo.InvariantCulture)}"])).Trim()).Replace(Path.DirectorySeparatorChar, '/');
        async Task<string> NativeAsync(string tool, string[] args)
        {
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BEETSDIR"] = config;
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Token);
            var stderr = process.StandardError.ReadToEndAsync(Token);
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return await stdout;
        }

        async Task RunBatchAsync()
        {
            ApproveLibraryBatchRequest approved;
            bool rejectedCollision;
            bool collisionRetained;
            bool privateCheckpoint;
            var replacementRejected = false;
            await using (var host = Host(interrupted))
            {
                await host.StartAsync(Token);
                using var client = Client(host, secret);
                var rejected = await MakeApproval(client, tagsFirst: true);
                Directory.CreateDirectory(Path.GetDirectoryName(Music(collision))!);
                await File.WriteAllTextAsync(Music(collision), "foreign destination", Token);
                await SubmitBatch(client, rejected);
                var refused = await BatchStatus(client, rejected.OperationId, terminal: true);
                collisionRetained = await File.ReadAllTextAsync(Music(collision), Token) == "foreign destination";
                rejectedCollision = refused.Phase == BatchPublicationPhase.Rejected && refused.FailureCode == "organization-destination-occupied" &&
                    (await Feed(client, 42)).Revisions.IsEmpty && observedEdits.TagPaths.Count == 0 && attempts == 0 && audioDeletes.Count == 0;
                File.Delete(Music(collision)); // Remove only the collision fixture this test created.

                if (removeAudio)
                {
                    var changed = await MakeApproval(client, tagsFirst: false);
                    const string replacement = "different scratch bytes after audio approval preview";
                    await File.WriteAllTextAsync(Music(otherSource), replacement, Token);
                    await SubmitBatch(client, changed);
                    var stale = await BatchStatus(client, changed.OperationId, terminal: true);
                    replacementRejected = stale.Phase == BatchPublicationPhase.Rejected && stale.FailureCode == "audio-removal-baseline-changed" &&
                        await File.ReadAllTextAsync(Music(otherSource), Token) == replacement && (await Feed(client, 42)).Revisions.IsEmpty &&
                        observedEdits.TagPaths.Count == 0 && attempts == 0 && audioDeletes.Count == 0;
                    await File.WriteAllBytesAsync(Music(otherSource), otherBytes, Token);
                }

                approved = await MakeApproval(client, tagsFirst: false);
                if (removeAudio)
                {
                    var unapproved = approved with { Entries = [.. approved.Entries.Select(entry =>
                        entry.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveAudio } deletion
                            ? entry with { Change = deletion with { AudioDeletionApproved = false } } : entry)] };
                    using var response = await client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), unapproved, Token);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.Empty((await Feed(client, 42)).Revisions);
                }
                await SubmitBatch(client, approved);
                await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
                var runningStatus = await BatchStatus(client, approved.OperationId, terminal: false);
                privateCheckpoint = runningStatus.Phase == BatchPublicationPhase.Running && (await Feed(client, 42)).Revisions.IsEmpty &&
                    await NativePath(harborNative, source) == destination && audioDeletes.Count == 0 &&
                    observedEdits.TagPaths.SequenceEqual(removeAudio ? [otherSource] : Array.Empty<string>());
                await host.StopAsync(Token);
            }
            using (var db = new LiteDatabase(hostDb))
                privateCheckpoint &= db.GetCollection<BsonDocument>("catalog_tracks").FindById(harbor)["RelativePath"].AsString == source &&
                    db.GetCollection<BsonDocument>("saved_playlists").Count() == 0;

            CatalogFeedPage page;
            ApprovedLibraryBatchStatus published;
            ApprovedLibraryBatchStatus replayed;
            await using (var host = Host(null))
            {
                await host.StartAsync(Token);
                try
                {
                    using var client = Client(host, secret);
                    using var peerClient = Client(host, peerToken);
                    published = await BatchStatus(client, approved.OperationId, terminal: true);
                    Assert.Equal(BatchPublicationPhase.Published, published.Phase);
                    page = await Feed(client, 42);
                    replayed = await SubmitBatch(client, approved);
                    using var desktop = LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: false);
                    var handler = new CatalogReplicaHandlers(library, peer, new HttpCatalogReplicaHost(peerClient, peerClient.BaseAddress!, library, peer,
                        new TokenProvider(library, peer, peerToken)), desktop);
                    var result = await handler.HandleAsync(new ResumeReplication(library, peer, ReplicaRecoveryMode.Incremental, 42),
                        new(new(Guid.NewGuid(), peer), Guid.NewGuid()), Token);
                    Assert.Equal(43L, Assert.IsType<Outcome<ReplicationState>.Success>(result).Value.Cursor);
                }
                finally
                {
                    // 2026-11-26: Stop the resumed native worker before fixture teardown for TEST-BEETS-002 and FR-LIBRARY-002#ac-batch-commit.
                    await host.StopAsync(CancellationToken.None);
                }
            }

            var retainedHarbor = await File.ReadAllBytesAsync(Music(source), Token);
            var taggedCopy = await File.ReadAllBytesAsync(Music(destination), Token);
            var removedCopy = await File.ReadAllBytesAsync(Music(collision), Token);
            var gone = beets is null ? observedEdits.Removed : string.IsNullOrWhiteSpace(await NativeAsync(beets,
                ["-c", settings, "list", "-f", "${id}", $"id:{nightNative.ToString(CultureInfo.InvariantCulture)}"]));
            var genre = await ReadGenre(destination, source);
            using var peerState = new LiteDatabase(peerDb);
            var tracks = peerState.GetCollection<BsonDocument>("catalog_tracks");
            var projected = tracks.FindById(harbor);
            var playlistId = Assert.Single(published.Playlists).PlaylistId;
            var playlist = peerState.GetCollection<BsonDocument>("catalog_playlists").FindById(playlistId);
            var peerProjection = projected["RelativePath"].AsString == destination && projected["Genre"].AsString == "Downtempo" && tracks.FindById(night) is null &&
                playlist["Entries"].AsArray.Select(entry => entry.AsDocument["TrackId"].AsGuid).SequenceEqual([harbor, harbor]);
            var revision = Assert.Single(page.Revisions);
            Assert.Equal(approved.OperationId, revision.OperationId);
            var retry = replayed.OperationId == approved.OperationId && replayed.CatalogRevision == published.CatalogRevision && replayed.Playlists.SequenceEqual(published.Playlists);
            if (removeAudio)
            {
                Assert.Equal(["Track:Upsert", "Track:Delete", "Playlist:Upsert"], revision.Changes.Select(change => $"{change.Kind}:{change.Operation}"));
                BatchAudioRemovalAssertions.Verify(new(privateCheckpoint, [.. observedEdits.TagPaths], observedEdits.RemovalPath!, [.. audioDeletes],
                    !File.Exists(Music(otherSource)) && gone && await ReadGenre(collision, otherSource) == "Jazz",
                    sourceBytes.SequenceEqual(retainedHarbor) && genre == "Downtempo" && !sourceBytes.SequenceEqual(taggedCopy),
                    peerProjection, page.Revisions.Length, revision.Revision, replacementRejected, rejectedCollision && collisionRetained, retry, attempts));
                return;
            }
            var retainedNight = await File.ReadAllBytesAsync(Music(otherSource), Token);
            BatchOrganizationAssertions.Verify(new(privateCheckpoint, attempts, Assert.Single(observedEdits.TagPaths), observedEdits.RemovalPath!,
                sourceBytes.SequenceEqual(retainedHarbor) && otherBytes.SequenceEqual(retainedNight), genre == "Downtempo" && !sourceBytes.SequenceEqual(taggedCopy),
                gone && otherBytes.SequenceEqual(removedCopy), page.Revisions.Length, revision.Revision,
                [.. revision.Changes.Select(change => $"{change.Kind}:{change.Operation}")], peerProjection, retry, rejectedCollision, collisionRetained));
        }

        async Task<string> ReadGenre(string relativePath, string original) => ffmpeg is null ? observedEdits.GenreFor(original) :
            Assert.Single((await NativeAsync(ffmpeg, ["-v", "error", "-i", Music(relativePath), "-f", "ffmetadata", "-"])).Split('\n'),
                line => line.StartsWith("GENRE=", StringComparison.OrdinalIgnoreCase))[6..].Trim();

        async Task<ApproveLibraryBatchRequest> MakeApproval(HttpClient client, bool tagsFirst)
        {
            var organization = await PreviewSelection(client, LibraryMaintenanceKind.Organize, 42, [harbor, night]);
            var removal = await PreviewSelection(client, removeAudio ? LibraryMaintenanceKind.RemoveAudio : LibraryMaintenanceKind.RemoveCatalog, 42, [night]);
            var edit = new ApprovedBatchChange.TrackTags(harbor, 42, ImmutableSortedDictionary<string, string>.Empty.Add("title", "Harbor Lights").Add("genre", "Ambient"),
                ImmutableSortedDictionary<string, string?>.Empty.Add("genre", "Downtempo"));
            ApprovedBatchChange move = new ApprovedBatchChange.Maintenance(organization, false);
            var changes = new List<ApprovedBatchChange>();
            if (removeAudio)
            {
                Assert.Equal(otherSource, Assert.Single(removal.AudioPaths));
                changes.Add(new ApprovedBatchChange.TrackTags(night, 42, ImmutableSortedDictionary<string, string>.Empty.Add("title", "Night Crossing").Add("genre", "Ambient"),
                    ImmutableSortedDictionary<string, string?>.Empty.Add("genre", "Jazz")));
            }
            changes.Add(tagsFirst ? edit : move);
            changes.Add(tagsFirst ? move : edit);
            changes.Add(new ApprovedBatchChange.Maintenance(removal, removeAudio));
            changes.Add(new ApprovedBatchChange.Playlist(new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), 1, null, null,
                "Approved organization", [new(Guid.NewGuid(), harbor), new(Guid.NewGuid(), harbor)]));
            return new(instance, Guid.NewGuid(), Guid.NewGuid(), 5, Guid.NewGuid(), [.. changes.Select((change, index) => new ApprovedBatchEntry(Guid.NewGuid(), index + 1, change))]);
        }

        async Task<ApprovedLibraryBatchStatus> SubmitBatch(HttpClient client, ApproveLibraryBatchRequest approval)
        {
            using var response = await client.PostAsJsonAsync(ApprovedLibraryBatchRoutes.ApprovePath(library), approval, Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            return (await response.Content.ReadFromJsonAsync<ApprovedLibraryBatchStatus>(Token))!;
        }

        // 2026-11-26: Match the native batch test deadline and report the last observed phase for TEST-BEETS-002 and FR-LIBRARY-002#ac-batch-commit.
        async Task<ApprovedLibraryBatchStatus> BatchStatus(HttpClient client, Guid operation, bool terminal)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(beets is null ? 25 : 60));
            ApprovedLibraryBatchStatus? status = null;
            try
            {
                while (true)
                {
                    status = (await client.GetFromJsonAsync<ApprovedLibraryBatchStatus>(ApprovedLibraryBatchRoutes.StatusPath(library, operation), limit.Token))!;
                    if (!terminal || status.Phase is BatchPublicationPhase.Published or BatchPublicationPhase.Rejected)
                        return status;
                    await Task.Delay(100, limit.Token);
                }
            }
            catch (OperationCanceledException error) when (limit.IsCancellationRequested && !Token.IsCancellationRequested)
            {
                throw new TimeoutException($"Organization batch {operation} remained in {status?.Phase.ToString() ?? "unknown"} " +
                    $"({status?.FailureCode ?? "no failure code"}) before the fixture deadline.", error);
            }
        }
    }

    private sealed record ApprovedMoveObservation(ImportJobSnapshot Job, bool Replay, int Revisions, bool Rejected, bool ForeignRetained);

    /// <summary>Stops the first host after the actual native path is verified.</summary>
    private sealed class InterruptOrganization(IBeetsOrganization inner, Action count, TaskCompletionSource? pause) : IBeetsOrganization
    {
        public Task<HostOutcome<OrganizationCopy>> InspectAsync(MaintenanceTrack track, MaintenancePathChange move, CancellationToken token) =>
            inner.InspectAsync(track, move, token);
        public Task<HostOutcome<NativeOrganizationState>> ObserveAsync(OrganizationCopy copy, CancellationToken token) => inner.ObserveAsync(copy, token);
        public Task<HostOutcome<OrganizationFileStamp>> ReadFileStampAsync(string relativePath, CancellationToken token) => inner.ReadFileStampAsync(relativePath, token);
        public async Task<HostOutcome<bool>> ApplyAsync(OrganizationCopy copy, CancellationToken token)
        {
            count();
            var result = await inner.ApplyAsync(copy, token);
            if (pause is not null && result is HostOutcome<bool>.Success { Value: true })
            {
                pause.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return result;
        }
    }

    /// <summary>Provides stable native observations when the optional real CLI fixtures are unavailable.</summary>
    private sealed class ScriptedOrganization(string music, string source, string destination, string otherSource, string collision)
        : IBeetsOrganization, IBeetsMaintenancePlanner
    {
        private readonly Dictionary<string, string> paths = new() { [source] = source, [otherSource] = otherSource };
        public string PathFor(string original) => paths[original];
        public Task<HostOutcome<ImmutableArray<MaintenancePathChange>>> PreviewMovesAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token) =>
            Task.FromResult<HostOutcome<ImmutableArray<MaintenancePathChange>>>(new HostOutcome<ImmutableArray<MaintenancePathChange>>.Success(
                [.. tracks.Select(track => new MaintenancePathChange(track.TrackId, track.RelativePath, track.RelativePath == source ? destination : collision))]));
        public Task<HostOutcome<ImmutableArray<AudioRemovalFile>>> PreviewAudioRemovalAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token) =>
            new ScriptedAudioRemoval(music).PreviewAudioRemovalAsync(tracks, token);
        public async Task<HostOutcome<OrganizationCopy>> InspectAsync(MaintenanceTrack track, MaintenancePathChange move, CancellationToken token)
        {
            if (File.Exists(Path.Combine(music, move.Destination)))
                return new HostOutcome<OrganizationCopy>.Rejected("organization-destination-occupied", Conflict: true);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(music, track.RelativePath), token);
            return new HostOutcome<OrganizationCopy>.Success(new(track, move.Destination, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        public Task<HostOutcome<NativeOrganizationState>> ObserveAsync(OrganizationCopy copy, CancellationToken token) =>
            Task.FromResult<HostOutcome<NativeOrganizationState>>(new HostOutcome<NativeOrganizationState>.Success(PathFor(copy.Track.RelativePath) == copy.Destination
                ? NativeOrganizationState.Destination : NativeOrganizationState.Source));
        public Task<HostOutcome<bool>> ApplyAsync(OrganizationCopy copy, CancellationToken token)
        {
            var target = Path.Combine(music, copy.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(music, copy.Track.RelativePath), target);
            paths[copy.Track.RelativePath] = copy.Destination;
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(true));
        }
        public async Task<HostOutcome<OrganizationFileStamp>> ReadFileStampAsync(string relativePath, CancellationToken token)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(music, relativePath), token);
            return new HostOutcome<OrganizationFileStamp>.Success(new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
    }

    /// <summary>Records the native paths received after organization and supplies scripted observations without CLI tools.</summary>
    private sealed class OrganizationBatchEdits(ScriptedOrganization organization, string source, string otherSource, Guid harbor, string music)
        : IBeetsTrackTags, IBeetsCatalogRemoval
    {
        private IBeetsTrackTags? tagAdapter;
        private IBeetsCatalogRemoval? removeAdapter;
        private readonly Dictionary<string, string> genres = new() { [source] = "Ambient", [otherSource] = "Ambient" };
        public List<string> TagPaths { get; } = [];
        public string? RemovalPath { get; private set; }
        public string GenreFor(string original) => genres[original];
        public bool Removed { get; private set; }
        public void UseNative(BeetsGuidedImport? native) { tagAdapter = native; removeAdapter = native; }

        public Task<HostOutcome<NativeTrackTags>> ObserveAsync(TrackTagWork work, CancellationToken token)
        {
            if (tagAdapter is not null)
                return tagAdapter.ObserveAsync(work, token);
            var original = work.TrackId == harbor ? source : otherSource;
            Assert.Equal(organization.PathFor(original), work.RelativePath);
            var fields = ImmutableDictionary<string, string>.Empty.Add("genre", GenreFor(original));
            return Task.FromResult<HostOutcome<NativeTrackTags>>(new HostOutcome<NativeTrackTags>.Success(new(fields, fields)));
        }

        public async Task<HostOutcome<bool>> ApplyAsync(TrackTagWork work, CancellationToken token)
        {
            TagPaths.Add(work.RelativePath);
            if (tagAdapter is not null)
                return await tagAdapter.ApplyAsync(work, token);
            var genre = work.Changes["genre"]!;
            genres[work.TrackId == harbor ? source : otherSource] = genre;
            await File.AppendAllTextAsync(Path.Combine(music, work.RelativePath), genre, token);
            return new HostOutcome<bool>.Success(true);
        }

        public Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            if (removeAdapter is not null)
                return removeAdapter.ObserveCatalogTrackAsync(track, token);
            var state = track.TrackId != harbor && Removed ? NativeCatalogTrackState.Missing :
                organization.PathFor(track.TrackId == harbor ? source : otherSource) == track.RelativePath ? NativeCatalogTrackState.Present : NativeCatalogTrackState.Diverged;
            return Task.FromResult<HostOutcome<NativeCatalogTrackState>>(new HostOutcome<NativeCatalogTrackState>.Success(state));
        }

        public Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            RemovalPath = track.RelativePath;
            if (removeAdapter is not null)
                return removeAdapter.RemoveCatalogTrackAsync(track, token);
            Removed = true;
            return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(true));
        }
    }
}
