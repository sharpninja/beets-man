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
    /// <summary>Interrupts removal after one real native operation and verifies the recovered publication and peer delivery records.</summary>
    /// <remarks>2026-11-24: Bind the native and host recovery of TEST-BEETS-002 for FR-BEETS-001#ac-edit, ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogRemovalPublishesOneRevisionAfterPartialNativeRestart(bool removeAudio)
    {
        const long initialRevision = 42;
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        Directory.CreateDirectory(Path.Combine(music, "Inbox"));
        var config = Directory.CreateDirectory(Path.Combine(root, "fixture-beets")).FullName;
        var nativeDatabase = Path.Combine(config, "library.db");
        var settings = Path.Combine(config, "config.yaml");
        var database = Path.Combine(root, "removal-execution.db");
        var stage = Path.Combine(root, "private");
        var work = Path.Combine(root, "work");
        var options = new BeetsImportOptions(beets ?? Environment.ProcessPath!, nativeDatabase, CreateCatalogIfMissing: beets is null);
        var names = new[] { CatalogRemovalExecutionAssertions.First, CatalogRemovalExecutionAssertions.Second, CatalogRemovalExecutionAssertions.Other };
        var titles = new[] { "Remove one", "Remove two", "Keep" };
        var ids = new Dictionary<long, string>();
        var audio = new Dictionary<string, byte[]>();
        if (beets is not null)
            await File.WriteAllTextAsync(settings, new SerializerBuilder().Build().Serialize(new Dictionary<string, object>
                { ["library"] = nativeDatabase, ["directory"] = music, ["plugins"] = Array.Empty<string>() }), Token);
        for (var index = 0; index < names.Length; index++)
        {
            var file = Path.Combine(music, names[index]);
            var id = 17L + index;
            if (beets is not null)
            {
                await RunTool(ffmpeg!, ["-v", "error", "-f", "lavfi", "-i", $"sine=frequency={440 + index}:duration=0.1", "-c:a", "flac",
                    "-metadata", $"title={titles[index]}", file]);
                await RunTool(beets, ["-c", settings, "import", "-C", "-M", "-W", "-P", "-I", "-A", "-s", "-q", "--", file]);
                id = long.Parse((await RunTool(beets, ["-c", settings, "list", "-f", "${id}", $"title:={titles[index]}"])).Trim(), CultureInfo.InvariantCulture);
            }
            else
                await File.WriteAllTextAsync(file, titles[index], Token);
            ids.Add(id, names[index]);
            audio.Add(names[index], await File.ReadAllBytesAsync(file, Token));
        }

        var trackIds = names.ToDictionary(name => name, _ => Guid.NewGuid());
        var revoke = Guid.NewGuid();
        var peer = Guid.NewGuid();
        const string peerCredential = "catalog-removal-fixture-peer";
        var operation = Guid.NewGuid();
        const string incorrectPath = "Moved/Keep.flac";
        var keepPath = removeAudio ? names[2] : incorrectPath;
        var mode = removeAudio ? LibraryMaintenanceKind.RemoveAudio : LibraryMaintenanceKind.RemoveCatalog;
        SeedHost(database, revokedInstance: revoke);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = initialRevision });
            var tracks = db.GetCollection<BsonDocument>("catalog_tracks");
            foreach (var (id, name) in ids)
                tracks.Insert(new BsonDocument
                {
                    ["_id"] = trackIds[name], ["LibraryId"] = library, ["BeetsTrackId"] = id, ["Revision"] = initialRevision,
                    ["Title"] = name, ["RelativePath"] = name == names[2] ? keepPath : name
                });
        }

        var scripted = new ScriptedCatalogRemoval(ids);
        var scriptedAudio = new ScriptedAudioRemoval(music);
        var halted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var fileDeletes = new List<string>();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        LibraryMaintenanceReceipt receipt;
        LibraryMaintenancePlan plan;
        ImportJobSnapshot interrupted;
        string[] nativeAfterInterruption;
        bool reviewedFilesRetained = false;
        bool deniedWithoutApproval = false;
        bool filesAtInterruption = false;
        await using (var host = BuildHost(stopAfterFirst: true))
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            plan = await Preview(client, initialRevision, trackIds[names[0]], trackIds[names[1]]);
            if (removeAudio)
            {
                reviewedFilesRetained = plan.AudioPaths.SequenceEqual(names.Take(2)) && audio.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(music, pair.Key))));
                using var missing = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, plan.PlanId),
                    new ConfirmLibraryMaintenanceRequest(instance, operation, mode, plan.CatalogRevision, AudioDeletionApproved: false), Token);
                deniedWithoutApproval = missing.StatusCode == HttpStatusCode.BadRequest;
            }
            receipt = await Confirm(client, plan, operation);
            await halted.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            interrupted = (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, receipt.JobId), Token))!;
            nativeAfterInterruption = await NativeCatalog();
            filesAtInterruption = audio.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(music, pair.Key))));
            await host.StopAsync(Token);
        }

        long interruptedRevision;
        int interruptedDeliveries;
        using (var db = new LiteDatabase(database))
        {
            interruptedRevision = db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
            interruptedDeliveries = db.GetCollection<BsonDocument>("notification_deliveries").Count();
            Assert.Equal(3, db.GetCollection<BsonDocument>("catalog_tracks").Count());
            Assert.True(db.GetCollection<BsonDocument>("library_jobs").FindById(receipt.JobId)["NativeAttempted"].AsBoolean);
        }

        ImportJobSnapshot recovered;
        ImportJobSnapshot diverged;
        bool exactReplay;
        bool deletesOnlyReviewed = false;
        const string replacement = "Reviewed path now holds different scratch bytes";
        await using (var host = BuildHost(stopAfterFirst: false))
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            recovered = await WaitForTerminal(client, receipt.JobId);
            exactReplay = receipt == await Confirm(client, plan, operation);
            Assert.True(exactReplay);
            if (removeAudio)
                deletesOnlyReviewed = names.Take(2).All(name => !File.Exists(Path.Combine(music, name))) &&
                    audio[names[2]].SequenceEqual(File.ReadAllBytes(Path.Combine(music, names[2])));
            var mismatched = await Preview(client, initialRevision + 1, trackIds[names[2]]);
            if (removeAudio)
                await File.WriteAllTextAsync(Path.Combine(music, names[2]), replacement, Token);
            var mismatchReceipt = await Confirm(client, mismatched, Guid.NewGuid());
            diverged = await WaitForTerminal(client, mismatchReceipt.JobId);
            await ApplyOnPeerAsync(new(host.Urls.Single()));
        }

        var observedNative = await NativeCatalog();
        using var final = new LiteDatabase(database);
        var publications = final.GetCollection<BsonDocument>("revision_changes").FindAll().ToArray();
        var publication = Assert.Single(publications);
        var tombstones = publication["Changes"].AsArray.Select(change => change.AsDocument).ToArray();
        var namesById = trackIds.ToDictionary(pair => pair.Value, pair => pair.Key);
        var trackDocs = final.GetCollection<BsonDocument>("catalog_tracks").FindAll().ToArray();
        var ledger = final.GetCollection<BsonDocument>("notification_deliveries").FindAll().ToArray();
        Assert.Equal(initialRevision + 1, Assert.Single(ledger)["Revision"].AsInt64);
        Assert.Equal("Acknowledged", Assert.Single(ledger)["Status"].AsString);
        if (removeAudio)
        {
            Assert.Equal(names.Take(2), calls);
            Assert.Equal(names.Take(2), fileDeletes);
            Assert.Equal(names[2], Assert.Single(observedNative));
            Assert.Equal(trackIds[names[2]], Assert.Single(trackDocs)["_id"].AsGuid);
            Assert.Equal(operation, publication["OperationId"].AsGuid);
            Assert.Equal(names.Take(2), tombstones.Select(row => namesById[row["Payload"].AsDocument["_id"].AsGuid]));
            AudioRemovalExecutionAssertions.Verify(new(reviewedFilesRetained, deniedWithoutApproval,
                interrupted.Phase == ImportJobPhase.Running && filesAtInterruption && interruptedRevision == initialRevision && interruptedDeliveries == 0 &&
                nativeAfterInterruption.SequenceEqual(names.Skip(1).Order(StringComparer.Ordinal)), deletesOnlyReviewed, calls.Count, fileDeletes.Count,
                publications.Length, recovered.CatalogRevision ?? 0, ledger[0]["RecipientInstanceId"].AsGuid == peer, exactReplay,
                diverged.Phase == ImportJobPhase.Failed && diverged.FailureCode == "audio-removal-baseline-changed" && !diverged.Retryable,
                await File.ReadAllTextAsync(Path.Combine(music, names[2]), Token) == replacement));
            return;
        }
        var preserved = audio.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(music, pair.Key))));
        CatalogRemovalExecutionAssertions.Verify(new(receipt.JobId, recovered.JobId, operation, publication["OperationId"].AsGuid,
            interrupted.Phase.ToString(), nativeAfterInterruption, interruptedRevision, interruptedDeliveries, recovered.Phase.ToString(),
            recovered.CatalogRevision ?? 0, publications.Length, [.. tombstones.Select(row => $"{row["Kind"].AsString}:{row["Operation"].AsString}:" +
                namesById[row["Payload"].AsDocument["_id"].AsGuid])], tombstones.All(row => row["Version"].AsInt32 == 1 &&
                row["Payload"].AsDocument["LibraryId"].AsGuid == library && row["Payload"].AsDocument["Revision"].AsInt64 == initialRevision + 1),
            observedNative, calls.ToArray(), preserved,
            trackDocs.Length == 1 && trackDocs[0]["_id"].AsGuid == trackIds[names[2]] && trackDocs[0]["RelativePath"].AsString == incorrectPath,
            peer, [.. ledger.Select(row => row["RecipientInstanceId"].AsGuid)], diverged.Phase.ToString(), diverged.FailureCode, diverged.Retryable));

        WebApplication BuildHost(bool stopAfterFirst)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            IBeetsCatalogRemoval native = beets is null ? scripted : new BeetsGuidedImport(options, music, work, new FileImportStage(stage));
            builder.Services.AddSingleton<IBeetsCatalogRemoval>(new CatalogRemovalInterruption(native, calls, stopAfterFirst ? halted : null));
            if (removeAudio)
            {
                if (beets is null)
                    builder.Services.AddSingleton<IBeetsMaintenancePlanner>(scriptedAudio);
                builder.Services.AddSingleton<IAudioRemovalFiles>(new CountAudioRemoval(beets is null ? scriptedAudio : (IAudioRemovalFiles)native, fileDeletes));
            }
            var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
                { [secret] = new(library, instance), [peerCredential] = new(library, peer), [RevokedCredential] = new(library, revoke) });
            return LibraryHostStartup.Build(builder, new(library, database, music, stage, work, [], Beets: options), verifier, credentials, credentials);
        }

        HttpClient Client(WebApplication host)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return client;
        }

        async Task<LibraryMaintenancePlan> Preview(HttpClient client, long revision, params Guid[] tracks)
        {
            using var reply = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.PreviewPath(library),
                new LibraryMaintenancePreviewRequest(mode, revision, [.. tracks]), Token);
            reply.EnsureSuccessStatusCode();
            return (await reply.Content.ReadFromJsonAsync<LibraryMaintenancePlan>(Token))!;
        }

        async Task<LibraryMaintenanceReceipt> Confirm(HttpClient client, LibraryMaintenancePlan preview, Guid id)
        {
            using var reply = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, preview.PlanId),
                new ConfirmLibraryMaintenanceRequest(instance, id, mode, preview.CatalogRevision, AudioDeletionApproved: removeAudio), Token);
            reply.EnsureSuccessStatusCode();
            return (await reply.Content.ReadFromJsonAsync<LibraryMaintenanceReceipt>(Token))!;
        }

        async Task<ImportJobSnapshot> WaitForTerminal(HttpClient client, Guid id)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                var job = (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, id), limit.Token))!;
                if (job.Phase is ImportJobPhase.Published or ImportJobPhase.Failed)
                    return job;
                await Task.Delay(100, limit.Token);
            }
        }

        async Task<string[]> NativeCatalog()
        {
            var paths = beets is null ? scripted.Current.Values.ToArray() : (await RunTool(beets, ["-c", settings, "list", "-p"]))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.GetRelativePath(music, path).Replace('\\', '/')).ToArray();
            return [.. paths.Order(StringComparer.Ordinal)];
        }

        async Task ApplyOnPeerAsync(Uri address)
        {
            var peerDb = Path.Combine(root, "removal-peer.db");
            using (LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: true)) { }
            using (var seed = new LiteDatabase(peerDb))
            {
                var tracks = seed.GetCollection<BsonDocument>("catalog_tracks");
                foreach (var name in names)
                    tracks.Insert(new BsonDocument { ["_id"] = trackIds[name], ["LibraryId"] = library, ["Title"] = name, ["RelativePath"] = name });
                seed.GetCollection<BsonDocument>("sync_checkpoints").Insert(new BsonDocument { ["_id"] = $"{library:N}/catalog",
                    ["LibraryId"] = library, ["Feed"] = "catalog", ["AppliedRevision"] = initialRevision });
            }
            using var peerHttp = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            peerHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", peerCredential);
            var pending = await peerHttp.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token);
            Assert.Equal([initialRevision + 1], pending!.PendingRevisions);
            using var local = LiteDbDesktopPlaylistStore.Open(peerDb, allowCreate: false);
            var replica = new CatalogReplicaHandlers(library, peer,
                new HttpCatalogReplicaHost(peerHttp, address, library, peer, new TokenProvider(library, peer, peerCredential)), local);
            var request = new RequestContext(Guid.NewGuid(), peer);
            var state = await replica.HandleAsync(new ResumeReplication(library, peer, ReplicaRecoveryMode.Incremental, initialRevision),
                new(request, Guid.NewGuid()), Token);
            Assert.Equal(initialRevision + 1, Assert.IsType<Outcome<ReplicationState>.Success>(state).Value.Cursor);
            var reader = new LiteDbCatalogReader(local, library, music, () => false);
            var queried = await reader.HandleAsync(new BrowseCatalog(CatalogCategory.Library, new(), new(CatalogSortKey.Title)), request, Token);
            Assert.Equal(trackIds[names[2]], Assert.Single(Assert.IsType<Outcome<CatalogPage>.Success>(queried).Value.Rows).Id);
            var acknowledged = await peerHttp.GetFromJsonAsync<CatalogNotifications>(CatalogReplicationRoutes.NotificationsPath(library), Token);
            Assert.Empty(acknowledged!.PendingRevisions);
        }

        async Task<string> RunTool(string tool, string[] arguments)
        {
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BEETSDIR"] = config;
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(Token);
            var error = process.StandardError.ReadToEndAsync(Token);
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }

    private sealed class CatalogRemovalInterruption(IBeetsCatalogRemoval inner, List<string> calls, TaskCompletionSource? halt) : IBeetsCatalogRemoval
    {
        public Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token) =>
            inner.ObserveCatalogTrackAsync(track, token);

        public async Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            calls.Add(track.RelativePath);
            var result = await inner.RemoveCatalogTrackAsync(track, token);
            if (halt is not null && result is HostOutcome<bool>.Success { Value: true })
            {
                halt.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return result;
        }
    }

    private sealed class ScriptedCatalogRemoval(Dictionary<long, string> tracks) : IBeetsCatalogRemoval
    {
        internal Dictionary<long, string> Current { get; } = new(tracks);

        public Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token) =>
            Task.FromResult<HostOutcome<NativeCatalogTrackState>>(new HostOutcome<NativeCatalogTrackState>.Success(
                !Current.TryGetValue(track.BeetsTrackId, out var path) ? NativeCatalogTrackState.Missing :
                path == track.RelativePath ? NativeCatalogTrackState.Present : NativeCatalogTrackState.Diverged));

        public Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token) =>
            Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Success(Current.Remove(track.BeetsTrackId)));
    }

    /// <summary>Counts file effects independently from the native catalog mutations.</summary>
    private sealed class CountAudioRemoval(IAudioRemovalFiles inner, List<string> calls) : IAudioRemovalFiles
    {
        public Task<HostOutcome<NativeAudioFileState>> ObserveAudioFileAsync(AudioRemovalFile file, CancellationToken token) => inner.ObserveAudioFileAsync(file, token);
        public Task<HostOutcome<bool>> DeleteAudioFileAsync(AudioRemovalFile file, CancellationToken token)
        {
            calls.Add(file.RelativePath);
            return inner.DeleteAudioFileAsync(file, token);
        }
    }

    /// <summary>Uses local scratch files when the optional native CLI test tools are unavailable.</summary>
    private sealed class ScriptedAudioRemoval(string music) : IAudioRemovalFiles, IBeetsMaintenancePlanner
    {
        public Task<HostOutcome<ImmutableArray<MaintenancePathChange>>> PreviewMovesAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token) =>
            throw new InvalidOperationException("This fixture only supports removal.");
        public async Task<HostOutcome<ImmutableArray<AudioRemovalFile>>> PreviewAudioRemovalAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token)
        {
            var files = ImmutableArray.CreateBuilder<AudioRemovalFile>(tracks.Length);
            foreach (var track in tracks)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(music, track.RelativePath), token);
                files.Add(new(track.RelativePath, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            return new HostOutcome<ImmutableArray<AudioRemovalFile>>.Success(files.MoveToImmutable());
        }
        public async Task<HostOutcome<NativeAudioFileState>> ObserveAudioFileAsync(AudioRemovalFile file, CancellationToken token)
        {
            var path = Path.Combine(music, file.RelativePath);
            if (!File.Exists(path))
                return new HostOutcome<NativeAudioFileState>.Success(NativeAudioFileState.Missing);
            var bytes = await File.ReadAllBytesAsync(path, token);
            var match = bytes.Length == file.Size && Convert.ToHexString(SHA256.HashData(bytes)) == file.Sha256;
            return new HostOutcome<NativeAudioFileState>.Success(match ? NativeAudioFileState.Present : NativeAudioFileState.Diverged);
        }
        public async Task<HostOutcome<bool>> DeleteAudioFileAsync(AudioRemovalFile file, CancellationToken token)
        {
            if (await ObserveAudioFileAsync(file, token) is not HostOutcome<NativeAudioFileState>.Success { Value: NativeAudioFileState.Present })
                return new HostOutcome<bool>.Success(false);
            File.Delete(Path.Combine(music, file.RelativePath));
            return new HostOutcome<bool>.Success(true);
        }
    }
}
