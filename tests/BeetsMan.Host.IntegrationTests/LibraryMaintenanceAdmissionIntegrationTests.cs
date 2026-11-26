using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    /// <summary>Verifies native dry-run paths and exact catalog-only or audio removal authorization across HTTPS and LiteDB.</summary>
    /// <remarks>2026-11-24: Bind STB18 and TEST-BEETS-002 for FR-BEETS-001#ac-edit and ac-recover.</remarks>
    /// <remarks>2026-11-26: Include organize and remove approval in the named FR-BEETS-001#ac-edit case.</remarks>
    internal async Task ReadOnlyMaintenancePreviewAndExactApprovalSurviveRestart()
    {
        const string relative = "Inbox/Waiting.flac";
        const string proposed = "Lena Vale/Northern Lines/01 Harbor Lights.flac";
        var beets = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS");
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG");
        Assert.Equal(beets is null, ffmpeg is null);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var inbox = Directory.CreateDirectory(Path.Combine(music, "Inbox")).FullName;
        var source = Path.Combine(inbox, "Waiting.flac");
        var sibling = Path.Combine(inbox, "Other.flac");
        var settings = Directory.CreateDirectory(Path.Combine(root, "fixture-beets")).FullName;
        var nativeDb = Path.Combine(settings, "library.db");
        var fixtureConfig = Path.Combine(settings, "config.yaml");
        const string template = "$artist/$album/$track $title";
        long nativeId = 27;
        string? initialNative = null;
        if (beets is not null)
        {
            await AudioAsync(source, "Harbor Lights", "1");
            await AudioAsync(sibling, "Night Crossing", "2");
            var configuration = new Dictionary<string, object>
                { ["library"] = nativeDb, ["directory"] = music, ["plugins"] = Array.Empty<string>() };
            await File.WriteAllTextAsync(fixtureConfig, new SerializerBuilder().Build().Serialize(configuration), Token);
            await NativeToolAsync(beets, ["-c", fixtureConfig, "import", "-C", "-M", "-W", "-P", "-I", "-A", "-q", "--flat", "--", inbox]);
            nativeId = long.Parse((await NativeToolAsync(beets, ["-c", fixtureConfig, "list", "-f", "${id}", "title:Harbor Lights"])).Trim(),
                CultureInfo.InvariantCulture);
            initialNative = await NativeToolAsync(beets, ["-c", fixtureConfig, "list", "-f", "${id}:${path}"]);
        }
        else
        {
            await File.WriteAllTextAsync(source, "selected catalog fixture", Token);
            await File.WriteAllTextAsync(sibling, "separate catalog fixture", Token);
        }
        var original = await File.ReadAllBytesAsync(source, Token);
        var other = await File.ReadAllBytesAsync(sibling, Token);
        var database = Path.Combine(root, "maintenance.db");
        var track = Guid.NewGuid();
        var secondDesktop = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = secondDesktop, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 42L });
            db.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
            {
                ["_id"] = track, ["LibraryId"] = library, ["Revision"] = 41L, ["Title"] = "Harbor Lights", ["BeetsTrackId"] = nativeId,
                ["RelativePath"] = relative
            });
        }

        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var conflict = new ConfirmLibraryMaintenanceRequest(instance, Guid.NewGuid(), LibraryMaintenanceKind.Organize, 42);
        LibraryMaintenancePlan organize;
        LibraryMaintenancePlan catalog;
        LibraryMaintenancePlan audio;
        LibraryMaintenanceReceipt moveReceipt;
        LibraryMaintenanceReceipt catalogReceipt;
        LibraryMaintenanceReceipt audioReceipt;
        string stale;
        string missingApproval;
        string swapped;
        int foreign;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var direct = Client(host, secret);
            organize = await PreviewAsync(direct, LibraryMaintenanceKind.Organize);
            catalog = await PreviewAsync(direct, LibraryMaintenanceKind.RemoveCatalog);
            audio = await PreviewAsync(direct, LibraryMaintenanceKind.RemoveAudio);
            Assert.NotEqual(catalog.PlanId, audio.PlanId);
            stale = await RejectAsync(direct, organize.PlanId, conflict with { ExpectedCatalogRevision = 41 }, HttpStatusCode.Conflict);
            var approveAudio = new ConfirmLibraryMaintenanceRequest(instance, Guid.NewGuid(), LibraryMaintenanceKind.RemoveAudio, 42, true);
            missingApproval = await RejectAsync(direct, audio.PlanId, approveAudio with { AudioDeletionApproved = false }, HttpStatusCode.BadRequest);
            swapped = await RejectAsync(direct, catalog.PlanId, approveAudio, HttpStatusCode.Conflict);
            using var another = Client(host, RevokedCredential);
            using var cross = await another.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, organize.PlanId),
                conflict with { OriginInstanceId = secondDesktop }, Token);
            foreign = (int)cross.StatusCode;

            moveReceipt = await ApproveAsync(direct, organize.PlanId, conflict);
            catalogReceipt = await ApproveAsync(direct, catalog.PlanId,
                conflict with { OperationId = Guid.NewGuid(), Kind = LibraryMaintenanceKind.RemoveCatalog });
            audioReceipt = await ApproveAsync(direct, audio.PlanId, approveAudio);
            Assert.Equal("maintenance-plan-already-admitted", await RejectAsync(direct, organize.PlanId, conflict with { OperationId = Guid.NewGuid() },
                HttpStatusCode.Conflict));
        }

        LibraryMaintenanceReceipt replayed;
        ImportJobSnapshot job;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var direct = Client(host, secret);
            replayed = await ApproveAsync(direct, organize.PlanId, conflict);
            job = (await direct.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, moveReceipt.JobId), Token))!;
        }

        var currentSource = await File.ReadAllBytesAsync(source, Token);
        var currentSibling = await File.ReadAllBytesAsync(sibling, Token);
        var nativeUnchanged = beets is null || initialNative == await NativeToolAsync(beets, ["-c", fixtureConfig, "list", "-f", "${id}:${path}"]);
        using (var db = new LiteDatabase(database))
        {
            var jobs = db.GetCollection<BsonDocument>("library_jobs").FindAll().ToArray();
            var target = db.GetCollection<BsonDocument>("catalog_tracks").FindById(track);
            var feed = db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
            Assert.Empty(db.GetCollection<BsonDocument>("revision_changes").FindAll());
            Assert.Empty(db.GetCollection<BsonDocument>("notification_deliveries").FindAll());
            Assert.Equal(3, db.GetCollection<BsonDocument>("operation_receipts").Count());
            Assert.Equal(audio.PlanId, jobs.Single(row => row["_id"].AsGuid == audioReceipt.JobId)["MaintenancePlanId"].AsGuid);
            Assert.True(jobs.Single(row => row["_id"].AsGuid == audioReceipt.JobId)["AudioDeletionApproved"].AsBoolean);
            LibraryMaintenanceAdmissionAssertions.Verify(new([.. organize.Moves.Select(move => $"{move.Source} -> {move.Destination}")],
                [.. organize.TrackIds], [track], [.. catalog.AudioPaths], [.. audio.AudioPaths], stale, missingApproval, swapped, foreign,
                moveReceipt.JobId, catalogReceipt.JobId, audioReceipt.JobId, replayed.JobId, job.Phase.ToString(),
                [.. jobs.Select(row => row["MaintenanceMode"].AsString).Order(StringComparer.Ordinal)],
                nativeUnchanged && original.SequenceEqual(currentSource) && other.SequenceEqual(currentSibling) &&
                !File.Exists(Path.Combine(music, proposed.Replace('/', Path.DirectorySeparatorChar))) && target["RelativePath"].AsString == relative, feed));
        }

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            builder.Services.AddSingleton<IBeetsCatalogRemoval>(new PausedCatalogRemoval());
            builder.Services.AddSingleton<IBeetsOrganization>(new PausedOrganization());
            if (beets is null)
                builder.Services.AddSingleton<IBeetsMaintenancePlanner>(new ScriptedMaintenancePlan(proposed));
            var options = beets is null ? null : new BeetsImportOptions(beets, nativeDb, template);
            return LibraryHostStartup.Build(builder, new(library, database, music, Path.Combine(root, "private"), Path.Combine(root, "work"), [], Beets: options),
                new Verifier(new(library, instance), secret, secondDesktop), credentials, credentials);
        }

        HttpClient Client(WebApplication host, string credential)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return client;
        }

        async Task<LibraryMaintenancePlan> PreviewAsync(HttpClient client, LibraryMaintenanceKind kind)
        {
            using var result = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.PreviewPath(library), new LibraryMaintenancePreviewRequest(kind, 42, [track]), Token);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync(Token));
            return (await result.Content.ReadFromJsonAsync<LibraryMaintenancePlan>(Token))!;
        }

        async Task<LibraryMaintenanceReceipt> ApproveAsync(HttpClient client, Guid planId, ConfirmLibraryMaintenanceRequest request)
        {
            using var result = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, planId), request, Token);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync(Token));
            return (await result.Content.ReadFromJsonAsync<LibraryMaintenanceReceipt>(Token))!;
        }

        async Task<string> RejectAsync(HttpClient client, Guid planId, ConfirmLibraryMaintenanceRequest request, HttpStatusCode status)
        {
            using var result = await client.PostAsJsonAsync(LibraryMaintenanceRoutes.ConfirmPath(library, planId), request, Token);
            Assert.Equal(status, result.StatusCode);
            return (await result.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString()!;
        }

        Task<string> AudioAsync(string path, string title, string number) => NativeToolAsync(ffmpeg!,
            ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.1", "-c:a", "flac", "-metadata", $"title={title}",
                "-metadata", "artist=Lena Vale", "-metadata", "album_artist=Lena Vale", "-metadata", "album=Northern Lines", "-metadata", $"track={number}", path]);

        async Task<string> NativeToolAsync(string executable, string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BEETSDIR"] = settings;
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Token);
            var stderr = process.StandardError.ReadToEndAsync(Token);
            await process.WaitForExitAsync(Token);
            Assert.True(process.ExitCode == 0, await stderr);
            return await stdout;
        }
    }

    private sealed class ScriptedMaintenancePlan(string destination) : IBeetsMaintenancePlanner
    {
        public Task<HostOutcome<ImmutableArray<MaintenancePathChange>>> PreviewMovesAsync(ImmutableArray<MaintenanceTrack> tracks,
            CancellationToken token) => Task.FromResult<HostOutcome<ImmutableArray<MaintenancePathChange>>>(
            new HostOutcome<ImmutableArray<MaintenancePathChange>>.Success([.. tracks.Select(track =>
                new MaintenancePathChange(track.TrackId, track.RelativePath, destination))]));

        public Task<HostOutcome<ImmutableArray<AudioRemovalFile>>> PreviewAudioRemovalAsync(ImmutableArray<MaintenanceTrack> tracks, CancellationToken token) =>
            Task.FromResult<HostOutcome<ImmutableArray<AudioRemovalFile>>>(new HostOutcome<ImmutableArray<AudioRemovalFile>>.Success(
                [.. tracks.Select(track => new AudioRemovalFile(track.RelativePath, 12, new string('F', 64)))]));
    }

    private sealed class PausedCatalogRemoval : IBeetsCatalogRemoval
    {
        public async Task<HostOutcome<NativeCatalogTrackState>> ObserveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HostOutcome<NativeCatalogTrackState>.Cancelled();
        }

        public Task<HostOutcome<bool>> RemoveCatalogTrackAsync(MaintenanceTrack track, CancellationToken token) =>
            throw new InvalidOperationException("The admission fixture must hold worker execution.");
    }

    /// <summary>Holds organization at inspection while this fixture measures admission alone.</summary>
    private sealed class PausedOrganization : IBeetsOrganization
    {
        public async Task<HostOutcome<OrganizationCopy>> InspectAsync(MaintenanceTrack track, MaintenancePathChange move, CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HostOutcome<OrganizationCopy>.Cancelled();
        }
        public Task<HostOutcome<NativeOrganizationState>> ObserveAsync(OrganizationCopy copy, CancellationToken token) =>
            throw new InvalidOperationException("The admission fixture must hold worker execution.");
        public Task<HostOutcome<bool>> ApplyAsync(OrganizationCopy copy, CancellationToken token) =>
            throw new InvalidOperationException("The admission fixture must hold worker execution.");
        public Task<HostOutcome<OrganizationFileStamp>> ReadFileStampAsync(string relativePath, CancellationToken token) =>
            throw new InvalidOperationException("The admission fixture must hold worker execution.");
    }
}
