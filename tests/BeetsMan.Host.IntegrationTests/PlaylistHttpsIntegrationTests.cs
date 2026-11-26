using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Api;
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
using DesktopSync = BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Runs the desktop queue publication through a real HTTPS endpoint and separate host and desktop stores.</summary>
/// <remarks>2026-11-24: Bind TEST-RECORDS-002 for authorization and FR-PLAYER-001#ac-queue-autosave for uncertain transport recovery.</remarks>
public sealed partial class PlaylistHttpsIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-https-playlist-{Guid.NewGuid():N}");
    private readonly Guid library = Guid.NewGuid();
    private readonly Guid instance = Guid.NewGuid();
    private readonly Guid playback = Guid.NewGuid();
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Publishes a reviewed STB20 player change after a real interruption of the file journal.</summary>
    /// <remarks>2026-11-24: Bind cross-profile checkout, isolation and recovery for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    [Fact]
    public async Task AndroidProfileAssignmentChangesOnlyWhenReviewedMigrationCommits()
    {
        var databasePath = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
        var otherExport = Directory.CreateDirectory(Path.Combine(root, "other-export")).FullName;
        var sourcePath = Path.Combine(music, "track.flac");
        await File.WriteAllTextAsync(sourcePath, "source audio", Token);
        const string oldRelative = "Unknown Artist/Unknown Album/track.flac";
        const string foreignRelative = "Unsafe/Foreign.flac";
        const string resolvedRelative = "Resolved/Track.flac";
        const string playlistRelative = "playlists/Ready.m3u8";
        var foreign = Path.Combine(export, foreignRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        await File.WriteAllTextAsync(foreign, "foreign audio", Token);
        var syncthing = new StartupSyncthingApi(new Dictionary<string, string> { ["android-a"] = export, ["android-b"] = otherExport });
        await using var fixture = syncthing.Build();
        await fixture.StartAsync(Token);
        var sync = new Uri(fixture.Urls.Single() + "/");
        var target = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "android-a", new(sync, "android-a/"), false);
        var other = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-b", "android-b", new(sync, "android-b/"), false);
        SeedHost(databasePath, [target, other]);
        var track = Guid.NewGuid();
        using (var database = new LiteDatabase(databasePath))
            database.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument { ["_id"] = track, ["LibraryId"] = library, ["RelativePath"] = "track.flac" });
        Guid playlistId;
        using (var store = LiteDbHostPlaylistStore.Open(databasePath, false))
            playlistId = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await new HostPlaylistWriter(store).SaveAsync(library,
                new(library, instance), new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                    "Ready", [new(Guid.NewGuid(), track), new(Guid.NewGuid(), track)]), Token)).Value.PlaylistId;
        using var certificate = CreateCertificate();
        var sender = new Uri(sync, "sender/");
        var options = new LibraryHostOptions(library, databasePath, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [new(target.DeviceId, target.ShareId, new(sender, StartupSyncthingApi.SenderId, export)),
                new(other.DeviceId, other.ShareId, new(sender, StartupSyncthingApi.SenderId, otherExport))]);
        var credentials = new StartupSyncthingCredentials(library, target.DeviceId, other.DeviceId);
        var identity = new Verifier(new(library, instance), secret, null);
        var request = new RequestContext(Guid.NewGuid(), instance);
        CommandContext Operation() => new(request, Guid.NewGuid());

        var profiles = new List<string>();
        var interrupt = new ExportInterruption();
        DesktopSync.PublishDeviceExport confirmed;
        CommandContext confirmationAttempt;
        DesktopSync.DeviceExportPlan original, published;
        string reviewedProfile, blockedPath, conflictingTarget;
        string[] removals;
        bool unapprovedUnchanged, replayedPreview, interrupted;
        await using (var app = Build())
        {
            await app.StartAsync(Token);
            var address = new Uri(app.Urls.Single());
            using var transport = new LostResponseHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(transport) { BaseAddress = address };
            var adapter = Gateway(client, address);
            var dispatcher = Dispatcher(adapter);
            original = await StageAsync(target, "musicolet", 2);
            var initial = Value(await dispatcher.SendAsync(new DesktopSync.PrepareDeviceExportPreview(target.DeviceId, "musicolet", 2,
                original.PlanId, original.PlanRevision), Operation(), Token));
            var initialPublication = Assert.IsType<DesktopSync.DeviceExportSubmission.Published>(Value(await dispatcher.SendAsync(
                new DesktopSync.PublishDeviceExport(initial.Plan.PlanId!.Value, initial.Plan.PlanRevision!.Value, null), Operation(), Token)));
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var state = Value(await adapter.ReadTransferAsync(new(initialPublication.Receipt.TransferId!.Value), request, Token));
                if (state.Phase == DesktopSync.TransferPhase.Complete)
                    break;
                await Task.Delay(25, Token);
            }
            Assert.Equal(DesktopSync.TransferPhase.Complete,
                Value(await adapter.ReadTransferAsync(new(initialPublication.Receipt.TransferId!.Value), request, Token)).Phase);
            profiles.Add(await ReadActiveAsync(adapter));

            var otherOriginal = await StageAsync(other, "musicolet", 2);
            _ = await StageAsync(other, "poweramp", 3);
            conflictingTarget = Assert.IsType<Outcome<DesktopSync.DeviceExportPreview>.Conflict>(await dispatcher.SendAsync(
                new DesktopSync.PrepareDeviceExportPreview(other.DeviceId, "poweramp", 3, otherOriginal.PlanId, otherOriginal.PlanRevision),
                Operation(), Token)).Code;

            var presentation = new PresentationServices(dispatcher, new InlineScheduler(), instance);
            using var syncList = new SyncListViewModel(presentation, Guid.NewGuid(), new(target.DeviceId, "musicolet", 2));
            syncList.Activate(syncList.Scope.OwnerId);
            await syncList.OpenCommand.ExecuteAsync(initial.Plan.PlanId);
            using var review = new DeviceExportViewModel(presentation, Guid.NewGuid(), syncList);
            review.Activate(review.Scope.OwnerId);
            await review.SelectLayoutCommand.ExecuteAsync(null);
            var profile = review.Layouts!.Profiles.Single(item => item.ProfileId == "poweramp");
            await review.PreviewCommand.ExecuteAsync(new LayoutPreviewInput(profile,
                new(ImmutableDictionary<Guid, string>.Empty.Add(track, foreignRelative))));
            var blocked = Assert.IsType<DesktopSync.DeviceExportPreview>(review.PreviewResult);
            blockedPath = Assert.Single(blocked.BlockingIssues).Field!;
            Assert.False(review.PublishCommand.CanExecute(null));

            var resolved = new DesktopSync.LayoutEdits(ImmutableDictionary<Guid, string>.Empty.Add(track, resolvedRelative));
            await review.ResolveLayoutCollisionCommand.ExecuteAsync(new LayoutPreviewInput(profile, resolved));
            await review.PublishCommand.ExecuteAsync(null);
            var proposal = Assert.IsType<DesktopSync.OwnedRemovalProposal>(review.RemovalProposal);
            Assert.Equal([oldRelative], proposal.Paths);
            Assert.True(review.CancelPreviewCommand.CanExecute(null));
            review.CancelPreviewCommand.Execute(null);
            Assert.Null(review.PreviewResult);
            Assert.Equal("musicolet", syncList.Target.ProfileId);
            profiles.Add(await ReadActiveAsync(adapter));

            var next = new DesktopSync.PrepareDeviceExportPreview(target.DeviceId, "poweramp", 3, syncList.Plan!.PlanId, syncList.Plan.PlanRevision, resolved);
            var reviewAttempt = Operation();
            transport.DropNextSuccess = true;
            var response = await dispatcher.SendAsync(next, reviewAttempt, Token);
            var recovered = Value(await dispatcher.SendAsync(next, reviewAttempt, Token));
            replayedPreview = response is Outcome<DesktopSync.DeviceExportPreview>.DependencyFailure { Retryable: true } &&
                recovered.Plan.PlanRevision == next.ExpectedPlanRevision + 1;
            reviewedProfile = $"{recovered.Plan.ProfileId}:{recovered.Plan.ProfileRevision}";
            removals = [.. recovered.ProposedRemovals];
            var draft = new DesktopSync.PublishDeviceExport(recovered.Plan.PlanId!.Value, recovered.Plan.PlanRevision!.Value, null);
            var awaiting = Assert.IsType<DesktopSync.DeviceExportSubmission.ConfirmationRequired>(
                Value(await dispatcher.SendAsync(draft, Operation(), Token)));
            profiles.Add(await ReadActiveAsync(adapter));
            var priorContent = await File.ReadAllTextAsync(Path.Combine(export, playlistRelative), Token);
            unapprovedUnchanged = File.Exists(Path.Combine(export, oldRelative)) && !File.Exists(Path.Combine(export, resolvedRelative)) &&
                priorContent.Contains("../" + oldRelative, StringComparison.Ordinal);
            confirmed = draft with { RemovalApproval = new(awaiting.Proposal.ProposalId, awaiting.Proposal.PlanId, awaiting.Proposal.PlanRevision) };
            confirmationAttempt = Operation();
            interrupt.Armed = true;
            var failed = await dispatcher.SendAsync(confirmed, confirmationAttempt, Token);
            interrupted = failed is Outcome<DesktopSync.DeviceExportSubmission>.DependencyFailure { Code: "export-test-interrupted", Retryable: true } &&
                interrupt.Calls == 1 && File.Exists(Path.Combine(export, resolvedRelative)) &&
                await File.ReadAllTextAsync(Path.Combine(export, playlistRelative), Token) == priorContent &&
                await ReadActiveAsync(adapter) == "musicolet:2";

            async Task<DesktopSync.DeviceExportPlan> StageAsync(RegisteredAndroidExport destination, string selected, long version) =>
                Value(await dispatcher.SendAsync(new DesktopSync.StagePlaylistForDevice(playlistId, destination.DeviceId, selected, version, null, null),
                    Operation(), Token));
        }

        await using (var app = Build())
        {
            await app.StartAsync(Token);
            var address = new Uri(app.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate));
            var adapter = Gateway(client, address);
            Assert.IsType<DesktopSync.DeviceExportSubmission.Published>(Value(await Dispatcher(adapter).SendAsync(confirmed, confirmationAttempt, Token)));
            profiles.Add(await ReadActiveAsync(adapter));
            published = Value(await adapter.ReadPlanAsync(new(target.DeviceId, null, null, null), request, Token));
            Assert.Equal("poweramp", published.ProfileId);
        }
        await using (var app = Build())
        {
            await app.StartAsync(Token);
            var address = new Uri(app.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate));
            profiles.Add(await ReadActiveAsync(Gateway(client, address)));
        }

        var lines = await File.ReadAllLinesAsync(Path.Combine(export, playlistRelative), Token);
        var files = Directory.EnumerateFiles(export, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(export, path).Replace('\\', '/')).Where(path => path != foreignRelative).Order(StringComparer.Ordinal).ToArray();
        StageAssertions.VerifyProfileChange(new([.. profiles], reviewedProfile, blockedPath, removals, [.. lines.Skip(1)], unapprovedUnchanged,
            conflictingTarget, replayedPreview, interrupted, files,
            File.Exists(foreign) && await File.ReadAllTextAsync(foreign, Token) == "foreign audio" &&
            await File.ReadAllTextAsync(sourcePath, Token) == "source audio" && await File.ReadAllTextAsync(Path.Combine(export, resolvedRelative), Token) == "source audio",
            original.PlanId == published.PlanId && original.Entries.Select(item => item.EntryId).SequenceEqual(published.Entries.Select(item => item.EntryId))));

        WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            builder.Services.AddSingleton<IDeviceExportStepWriter>(provider => new InterruptedExportWriter(interrupt, work => new FileDeviceExportStepWriter(
                work.Destination.DeviceId == target.DeviceId ? export : otherExport, options.PublicationWorkRoot, work.Destination,
                provider.GetRequiredService<IDeviceExportPayloadStore>())));
            return LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        }
        HttpDevicePlaylistExportHost Gateway(HttpClient client, Uri address) => new(client, address, library, new TokenProvider(library, instance, secret));
        async Task<string> ReadActiveAsync(HttpDevicePlaylistExportHost adapter)
        {
            var profile = Value(await adapter.ReadLayoutsAsync(new(target.DeviceId), request, Token)).ActiveProfile;
            return $"{profile?.ProfileId}:{profile?.Revision}";
        }
        static RequestDispatcher Dispatcher(HttpDevicePlaylistExportHost adapter)
        {
            var queries = new DesktopSync.DeviceExportQueryHandlers(adapter);
            var builder = new RequestDispatcher.Builder().RegisterCommand(new DesktopSync.StagePlaylistForDeviceHandler(adapter))
                .RegisterQuery<DesktopSync.GetDeviceExportPlan, DesktopSync.DeviceExportPlan>(queries)
                .RegisterQuery<DesktopSync.GetDeviceLayouts, DesktopSync.DeviceLayoutsSnapshot>(queries);
            return new DesktopSync.DeviceExportCommandHandlers(adapter).Register(builder).Build();
        }
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    /// <summary>Runs STB10 direct drops and removals through the desktop and the configured HTTPS/LiteDB host.</summary>
    /// <remarks>2026-11-24: Bind the mock-proven staged-list assertions for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    [Fact]
    public async Task SyncListEditsPreserveSharedAudioAndInvalidateReviewWithoutTouchingMedia()
    {
        var databasePath = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
        var existingPath = Path.Combine(Directory.CreateDirectory(Path.Combine(export, "Unknown Artist", "Unknown Album")).FullName, "present.flac");
        await File.WriteAllTextAsync(existingPath, "already present", Token);
        var syncApi = new StartupSyncthingApi(new Dictionary<string, string> { ["android-a"] = export });
        syncApi.Scanned.Enqueue("android-a");
        await using var fixture = syncApi.Build();
        await fixture.StartAsync(Token);
        var sync = new Uri(fixture.Urls.Single() + "/");
        var target = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "android-a", new(sync, "android-a/"), false);
        SeedHost(databasePath, [target]);
        var tracks = new[] { "a", "b", "c", "d", "present" }.ToDictionary(name => name, _ => Guid.NewGuid(), StringComparer.Ordinal);
        foreach (var name in tracks.Keys.Where(name => name != "present"))
            await File.WriteAllTextAsync(Path.Combine(music, name + ".flac"), "source " + name, Token);
        using (var database = new LiteDatabase(databasePath))
        {
            foreach (var (name, id) in tracks)
                database.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                    { ["_id"] = id, ["LibraryId"] = library, ["RelativePath"] = name + ".flac" });
        }
        var saved = new Dictionary<string, Guid>();
        using (var store = LiteDbHostPlaylistStore.Open(databasePath, false))
        {
            var writer = new HostPlaylistWriter(store);
            await SaveAsync("First", ["a", "b", "c", "d"]);
            await SaveAsync("Second", ["c"]);

            async Task SaveAsync(string name, string[] ordered)
            {
                var request = new PlaylistSaveRequest(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                    name, [.. ordered.Select(key => new PlaylistEntryData(Guid.NewGuid(), tracks[key]))]);
                var result = await writer.SaveAsync(library, new(library, instance), request, Token);
                saved.Add(name, Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(result).Value.PlaylistId);
            }
        }
        using var certificate = CreateCertificate();
        var options = new LibraryHostOptions(library, databasePath, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [new(target.DeviceId, target.ShareId, new(new(sync, "sender/"), StartupSyncthingApi.SenderId, export))]);
        var credentials = new StartupSyncthingCredentials(library, target.DeviceId, Guid.Empty);
        var identity = new Verifier(new(library, instance), secret, null);
        var context = new RequestContext(Guid.NewGuid(), instance);
        CommandContext StartCommand() => new(context, Guid.NewGuid());
        DeviceSyncListEditResponse replay;
        DesktopSync.DeviceExportPlan dropped, firstRemoval, lastRemoval;
        bool responseLost, sameReceipt, oldPreviewRejected;
        int foreignStatus;
        string staleCode, changedIntent;
        string[] previewFiles;
        await using (var app = Build())
        {
            await app.StartAsync(Token);
            var address = new Uri(app.Urls.Single());
            using var transport = new LostResponseHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(transport) { BaseAddress = address };
            var adapter = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
            var queries = new DesktopSync.DeviceExportQueryHandlers(adapter);
            var builder = new RequestDispatcher.Builder().RegisterCommand(new DesktopSync.StagePlaylistForDeviceHandler(adapter))
                .RegisterQuery<DesktopSync.GetDeviceExportPlan, DesktopSync.DeviceExportPlan>(queries);
            var dispatcher = new DesktopSync.DeviceExportCommandHandlers(adapter).Register(new DesktopSync.DeviceSyncListEditHandlers(adapter)
                .Register(builder)).Build();
            var drop = new DesktopSync.StageTracksForDevice([tracks["b"], tracks["a"], tracks["b"], tracks["present"]], target.DeviceId, "musicolet", 2, null, null);
            var dropContext = StartCommand();
            transport.DropNextSuccess = true;
            responseLost = (await dispatcher.SendAsync(drop, dropContext, Token)) is Outcome<DesktopSync.DeviceExportPlan>.DependencyFailure { Retryable: true };
            dropped = Value(await dispatcher.SendAsync(drop, dropContext, Token));
            changedIntent = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Conflict>(
                await dispatcher.SendAsync(drop with { OrderedTrackIds = [tracks["a"]] }, dropContext, Token)).Code;
            var planId = dropped.PlanId!.Value;
            var plan = dropped;
            foreach (var name in new[] { "First", "Second" })
                plan = Value(await dispatcher.SendAsync(new DesktopSync.StagePlaylistForDevice(saved[name], target.DeviceId, "musicolet", 2,
                    planId, plan.PlanRevision), StartCommand(), Token));
            var previous = Value(await dispatcher.SendAsync(new DesktopSync.PrepareDeviceExportPreview(target.DeviceId, "musicolet", 2,
                planId, plan.PlanRevision), StartCommand(), Token));
            var removeIds = ImmutableArray.Create(plan.Entries.OfType<DesktopSync.ExportEntry.Playlist>().Single(item => item.PlaylistId == saved["First"]).EntryId,
                plan.Entries.OfType<DesktopSync.ExportEntry.Audio>().Single(item => item.TrackId == tracks["a"]).EntryId);
            var previousRevision = previous.Plan.PlanRevision!.Value;
            using (var message = new HttpRequestMessage(HttpMethod.Post, DeviceSyncListEditRoutes.RemovePath(Guid.NewGuid()))
            {
                Content = JsonContent.Create(new RemoveDeviceExportEntriesRequest(instance, Guid.NewGuid(), planId, removeIds, previousRevision))
            })
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using var foreign = await client.SendAsync(message, Token);
                foreignStatus = (int)foreign.StatusCode;
            }
            var remove = new DesktopSync.EditDeviceExportPlan(planId, new DesktopSync.DeviceExportEdit.Remove(removeIds), previousRevision);
            var removeContext = StartCommand();
            firstRemoval = Value(await dispatcher.SendAsync(remove, removeContext, Token));
            staleCode = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Conflict>(
                await dispatcher.SendAsync(remove, StartCommand(), Token)).Code;
            oldPreviewRejected = await dispatcher.SendAsync(new DesktopSync.PublishDeviceExport(planId, previousRevision, null), StartCommand(), Token)
                is Outcome<DesktopSync.DeviceExportSubmission>.Conflict { Code: "export-plan-changed" };

            using var viewModel = new SyncListViewModel(new(dispatcher, new InlineScheduler(), instance), Guid.NewGuid(),
                new(target.DeviceId, "musicolet", 2));
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenCommand.ExecuteAsync(planId);
            Assert.Equal(firstRemoval.PlanRevision, viewModel.Plan?.PlanRevision);
            var sharedId = firstRemoval.Entries.OfType<DesktopSync.ExportEntry.Audio>().Single(entry => entry.TrackId == tracks["c"]).EntryId;
            await viewModel.AddSelectedCommand.ExecuteAsync(ImmutableArray.Create(tracks["c"]));
            Assert.Equal(firstRemoval.PlanRevision + 1, viewModel.Plan?.PlanRevision);
            await viewModel.RemoveStagedEntryCommand.ExecuteAsync(ImmutableArray.Create(sharedId));
            Assert.Contains(viewModel.Plan!.Entries, entry => entry.EntryId == sharedId);
            var secondId = firstRemoval.Entries.OfType<DesktopSync.ExportEntry.Playlist>().Single().EntryId;
            await viewModel.RemoveStagedEntryCommand.ExecuteAsync(ImmutableArray.Create(secondId));
            Assert.Null(viewModel.Failure);
            lastRemoval = Assert.IsType<DesktopSync.DeviceExportPlan>(viewModel.Plan);
            var replayRequest = new RemoveDeviceExportEntriesRequest(instance, removeContext.OperationId, planId, removeIds, previousRevision);
            using var replayMessage = new HttpRequestMessage(HttpMethod.Post, DeviceSyncListEditRoutes.RemovePath(library))
                { Content = JsonContent.Create(replayRequest) };
            replayMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var replayResponse = await client.SendAsync(replayMessage, Token);
            replayResponse.EnsureSuccessStatusCode();
            replay = Assert.IsType<DeviceSyncListEditResponse>(await replayResponse.Content.ReadFromJsonAsync<DeviceSyncListEditResponse>(Token));
            sameReceipt = replay.OperationId == removeContext.OperationId && replay.Plan.PlanRevision == firstRemoval.PlanRevision &&
                replay.Plan.Entries.Select(entry => entry.EntryId).SequenceEqual(firstRemoval.Entries.Select(entry => entry.EntryId));
            var preview = Value(await dispatcher.SendAsync(new DesktopSync.PrepareDeviceExportPreview(target.DeviceId, "musicolet", 2, planId,
                lastRemoval.PlanRevision), StartCommand(), Token));
            Assert.Empty(preview.BlockingIssues);
            previewFiles = [.. preview.Changes.Select(change => Path.GetFileName(change.After!))];
        }
        DesktopSync.DeviceExportPlan reloaded;
        await using (var app = Build())
        {
            await app.StartAsync(Token);
            var address = new Uri(app.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate));
            var adapter = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
            reloaded = Value(await adapter.ReadPlanAsync(new(target.DeviceId, dropped.PlanId, "musicolet", 2), context, Token));
        }

        StageAssertions.VerifySyncListEdits(new(Describe(dropped), Describe(firstRemoval), Describe(lastRemoval), Describe(reloaded), previewFiles,
            lastRemoval.Entries.Select(entry => entry.EntryId).SequenceEqual(new[] { dropped.Entries[0].EntryId, dropped.Entries[2].EntryId }) &&
            reloaded.Entries.Select(entry => entry.EntryId).SequenceEqual(lastRemoval.Entries.Select(entry => entry.EntryId)),
            responseLost && sameReceipt, staleCode, changedIntent, foreignStatus, oldPreviewRejected,
            Directory.EnumerateFiles(export, "*", SearchOption.AllDirectories).SequenceEqual([existingPath]) &&
            await File.ReadAllTextAsync(existingPath, Token) == "already present" && await File.ReadAllTextAsync(Path.Combine(music, "b.flac"), Token) == "source b"));

        WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        }

        string[] Describe(DesktopSync.DeviceExportPlan plan) => [.. plan.Entries.Select(entry => entry switch
        {
            DesktopSync.ExportEntry.Audio audio => $"{tracks.Single(pair => pair.Value == audio.TrackId).Key}:{audio.Status}",
            DesktopSync.ExportEntry.Playlist playlist => saved.Single(pair => pair.Value == playlist.PlaylistId).Key,
            _ => throw new InvalidOperationException()
        })];
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    /// <summary>Starts the standalone listener with reference-only JSON and separately scoped credentials.</summary>
    /// <remarks>2026-11-24: Bind the mock-proven launch assertions for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public async Task HostLauncherAuthenticatesExactDesktopAndKeepsSyncthingCredentialRolesSeparate()
    {
        var database = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
        var revoked = Guid.NewGuid();
        var receiver = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "android-a",
            new(new Uri("http://127.0.0.1:8384/"), "android-a/"), false);
        SeedHost(database, [receiver], revoked);
        using var certificate = CreateCertificate();
        var senderId = StartupSyncthingApi.SenderId;
        var host = new LibraryHostOptions(library, database, music, Path.Combine(root, "stage"), Path.Combine(root, "work"),
            [new(receiver.DeviceId, receiver.ShareId, new(new Uri("http://127.0.0.1:8384/"), senderId, export))]);
        var config = new LibraryHostLaunchSettings(host, new("127.0.0.1", 0, certificate.Thumbprint),
            new([new(instance, "client-active"), new(revoked, "client-revoked")],
                [new(receiver.DeviceId, "receiver-a")], [new(senderId, "sender-a")]));
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client-active"] = "fixture-active-client", ["client-revoked"] = "fixture-revoked-client",
            ["receiver-a"] = "fixture-receiver", ["sender-a"] = "fixture-sender"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var path = Path.Combine(root, "host-settings.json");
        await File.WriteAllTextAsync(path, json, Token);
        var settings = await LibraryHostLauncher.ReadSettingsAsync(path, Token);
        var vault = new FixtureHostVault(secrets);
        await using var app = await LibraryHostLauncher.BuildAsync(settings, certificate, vault, Token);
        await app.StartAsync(Token);
        using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(app.Urls.Single() + "/") };
        var route = DeviceExportQueryRoutes.LayoutsPath(library, receiver.DeviceId);
        var correct = await StatusAsync(route, secrets["client-active"]);
        var unknown = await StatusAsync(route, "fixture-unknown");
        var rejected = await StatusAsync(route, secrets["client-revoked"]);
        var senders = app.Services.GetRequiredService<IHostSenderTokenProvider>();
        var receivers = app.Services.GetRequiredService<IRegisteredSyncthingTokenProvider>();
        var senderCredential = await senders.GetTokenAsync(library, senderId, Token);
        var receiverCredential = await receivers.GetTokenAsync(library, receiver.DeviceId, Token);
        var otherLibrary = Guid.NewGuid();
        var otherStatus = await StatusAsync(DeviceExportQueryRoutes.LayoutsPath(otherLibrary, receiver.DeviceId), secrets["client-active"]);
        var otherSender = await senders.GetTokenAsync(otherLibrary, senderId, Token);
        var otherReceiver = await receivers.GetTokenAsync(otherLibrary, receiver.DeviceId, Token);
        var missing = await Record.ExceptionAsync(async () =>
        {
            await using var unused = await LibraryHostLauncher.BuildAsync(settings, certificate,
                new FixtureHostVault(new Dictionary<string, string>()), Token);
        });
        var observedJson = await File.ReadAllTextAsync(path, Token);
        await app.StopAsync(Token);
        Assert.Equal(1, vault.Requested.Count(reference => reference == "receiver-a"));

        StageAssertions.VerifyHostLaunch(new(client.BaseAddress.Scheme == Uri.UriSchemeHttps, correct, unknown, rejected,
            vault.Requested.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), senderCredential == secrets["sender-a"],
            receiverCredential == secrets["receiver-a"], otherStatus == 403 && otherSender is null && otherReceiver is null,
            missing is InvalidOperationException && missing.Message == "A configured desktop credential is missing or malformed.",
            observedJson == json && !secrets.Values.Any(value => observedJson.Contains(value, StringComparison.Ordinal))));

        async Task<int> StatusAsync(string requestPath, string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request, Token);
            return (int)response.StatusCode;
        }
    }

    /// <summary>Starts the shared host from configuration and sends two independent Android exports through its real workers.</summary>
    /// <remarks>2026-11-24: Bind the FR-FILESYNC-001#ac-layouts and ac-safety startup assertions for TEST-FILESYNC-002.</remarks>
    /// <remarks>2026-11-25: Observe the registered workspace following the published transfer and stopping its reads for FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public async Task HostStartupComposesIndependentAndroidExportsAndRestoresTheirReceipts()
    {
        var databasePath = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var exportA = Directory.CreateDirectory(Path.Combine(root, "export-a")).FullName;
        var exportB = Directory.CreateDirectory(Path.Combine(root, "export-b")).FullName;
        await File.WriteAllTextAsync(Path.Combine(music, "track.flac"), "startup audio", Token);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal) { ["android-a"] = exportA, ["android-b"] = exportB };
        var syncthing = new StartupSyncthingApi(folders);
        await using var syncApi = syncthing.Build();
        await syncApi.StartAsync(Token);
        var sync = new Uri(syncApi.Urls.Single() + '/');
        var first = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "android-a", new(sync, "android-a/"), false);
        var second = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-b", "android-b", new(sync, "android-b/"), false);
        SeedHost(databasePath, [first, second]);
        var track = Guid.NewGuid();
        using (var database = new LiteDatabase(databasePath))
            database.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = track, ["LibraryId"] = library, ["RelativePath"] = "track.flac" });
        var sender = new Uri(sync, "sender/");
        var privateStage = Path.Combine(root, "private-stage");
        var settings = new LibraryHostOptions(library, databasePath, music, privateStage, Path.Combine(privateStage, "retained"),
            [new(first.DeviceId, first.ShareId, new(sender, StartupSyncthingApi.SenderId, exportA)),
                new(second.DeviceId, second.ShareId, new(sender, StartupSyncthingApi.SenderId, exportB))]);
        var credentials = new StartupSyncthingCredentials(library, first.DeviceId, second.DeviceId);
        var identity = new Verifier(new(library, instance), secret, null);
        using var certificate = CreateCertificate();
        var rejected = Record.Exception(() => Startable(settings with { PrivateStageRoot = exportA }));
        var overlapRejected = rejected is ArgumentException && rejected.Message.Contains("must be disjoint", StringComparison.Ordinal);
        DesktopSync.ExportPublicationReceipt firstReceipt, secondReceipt;
        DesktopSync.MediaTransferState firstComplete, secondComplete;
        string[] profiles;
        bool started;
        int unauthorized;
        bool idle, followed, stoppedAtTerminal, cleared, cancelled, alerted;
        await using (var host = Startable(settings))
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            started = address.Scheme == Uri.UriSchemeHttps;
            using var tracking = new TransferReadTrackingHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(tracking) { BaseAddress = address };
            using var missing = await client.GetAsync(DeviceExportQueryRoutes.LayoutsPath(library, first.DeviceId), Token);
            unauthorized = (int)missing.StatusCode;
            var context = new RequestContext(Guid.NewGuid(), instance);
            var access = new TokenProvider(library, instance, secret);
            var exports = new HttpDevicePlaylistExportHost(client, address, library, access);
            var layouts = await exports.ReadLayoutsAsync(new(first.DeviceId), context, Token);
            profiles = [.. DesktopValue(layouts).Profiles.Select(profile => profile.ProfileId)];
            await using var desktop = new DesktopStartup(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "desktop"), music), [],
                library, instance, host: new(address, client, access), presentationScheduler: new InlineScheduler());
            var models = Assert.IsType<DesktopPresentation>(DesktopValue(await desktop.StartAsync(Token)).Presentation);
            var notification = new TaskCompletionSource<DesktopAlert>(TaskCreationOptions.RunContinuationsAsynchronously);
            models.Alerts.Raised += alert => notification.TrySetResult(alert);
            var complete = new TaskCompletionSource<DesktopSync.MediaTransferState>(TaskCreationOptions.RunContinuationsAsynchronously);
            models.Sync.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(MediaSyncViewModel.Transfer) && models.Sync.Transfer is { Phase: DesktopSync.TransferPhase.Complete } transfer)
                    complete.TrySetResult(transfer);
            };
            await models.Sync.LoadCommand.ExecuteAsync(first.DeviceId);
            idle = tracking.Reads == 0 && models.Sync.Transfer is null;
            firstReceipt = await PublishAsync(first, "First", "musicolet", 2);
            firstComplete = await complete.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            followed = firstComplete.TransferId == firstReceipt.TransferId && tracking.Reads > 0 && !models.Sync.IsInteractionBlocked;
            var delivered = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            alerted = delivered.Kind == DesktopAlertKind.SyncCompleted && delivered.SubjectId == firstComplete.TransferId;
            var reads = tracking.Reads;
            await Task.Delay(TimeSpan.FromMilliseconds(1250), Token);
            stoppedAtTerminal = tracking.Reads == reads;

            await models.Sync.LoadCommand.ExecuteAsync(second.DeviceId);
            cleared = models.Sync.Transfer is null;
            tracking.HoldNextRead();
            secondReceipt = await PublishAsync(second, "Second", "poweramp", 3);
            await tracking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            DesktopValue(await desktop.StopAsync(Token));
            var closedReads = tracking.Reads;
            await models.Sync.InspectTransferCommand.ExecuteAsync(secondReceipt.TransferId!.Value);
            cancelled = tracking.Cancelled.Task.IsCompletedSuccessfully && !models.Sync.IsActive && tracking.Reads == closedReads;
            secondComplete = await WaitForReceiverAsync(secondReceipt.TransferId!.Value);
            await host.StopAsync(Token);

            async Task<DesktopSync.ExportPublicationReceipt> PublishAsync(RegisteredAndroidExport target, string name, string profile, long profileRevision)
            {
                using var result = await PostAsync(client, library, new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()),
                    null, null, name, [new(Guid.NewGuid(), track)]), secret);
                Assert.Equal(HttpStatusCode.OK, result.StatusCode);
                var saved = await result.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token);
                Assert.NotNull(saved);
                Assert.Equal(target.DeviceId, models.Sync.SelectedPeerId);
                var layout = models.DeviceExport.Layouts!.Profiles.Single(item => item.ProfileId == profile && item.Revision == profileRevision);
                await models.DeviceExport.SelectStagingProfileCommand.ExecuteAsync(layout);
                await models.SyncList.DropSavedPlaylistCommand.ExecuteAsync(saved.PlaylistId);
                Assert.Null(models.SyncList.Failure);
                await models.DeviceExport.PreviewCommand.ExecuteAsync(new LayoutPreviewInput(layout));
                var preview = Assert.IsType<DesktopSync.DeviceExportPreview>(models.DeviceExport.PreviewResult);
                Assert.Empty(preview.BlockingIssues);
                await models.DeviceExport.PublishCommand.ExecuteAsync(null);
                return Assert.IsType<DesktopSync.ExportPublicationReceipt>(models.DeviceExport.Publication);
            }

            async Task<DesktopSync.MediaTransferState> WaitForReceiverAsync(Guid transfer)
            {
                var current = DesktopValue(await exports.ReadTransferAsync(new(transfer), context, Token));
                for (var attempt = 0; attempt < 100 && current.Phase is DesktopSync.TransferPhase.Queued or DesktopSync.TransferPhase.Running; attempt++)
                {
                    await Task.Delay(100, Token);
                    current = DesktopValue(await exports.ReadTransferAsync(new(transfer), context, Token));
                }
                Assert.True(current.Phase == DesktopSync.TransferPhase.Complete, $"Transfer {current.TransferId} reached {current.Phase} with {current.FailureCode}.");
                return current;
            }
        }

        bool storeReopened;
        await using (var reopened = Startable(settings))
        {
            await reopened.StartAsync(Token);
            var address = new Uri(reopened.Urls.Single());
            using var client = new HttpClient(CreateClientHandler(certificate));
            var gateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
            var context = new RequestContext(Guid.NewGuid(), instance);
            var a = DesktopValue(await gateway.ReadTransferAsync(new(firstReceipt.TransferId!.Value), context, Token));
            var b = DesktopValue(await gateway.ReadTransferAsync(new(secondReceipt.TransferId!.Value), context, Token));
            storeReopened = a == firstComplete && b == secondComplete;
        }

        var firstFile = PlaylistPath(exportA);
        var secondFile = PlaylistPath(exportB);
        Assert.Equal("#EXTM3U\n../Unknown Artist/Unknown Album/track.flac\n", await File.ReadAllTextAsync(Path.Combine(exportA, firstFile), Token));
        Assert.Equal("startup audio", await File.ReadAllTextAsync(Path.Combine(exportB, "Unknown Artist", "Unknown Album", "track.flac"), Token));
        StageAssertions.VerifyHostStartup(new(started, profiles, firstFile, secondFile,
            !File.Exists(Path.Combine(exportA, secondFile)), !File.Exists(Path.Combine(exportB, firstFile)), [.. syncthing.Scanned.Order(StringComparer.Ordinal)],
            firstComplete.Phase == DesktopSync.TransferPhase.Complete && secondComplete.Phase == DesktopSync.TransferPhase.Complete &&
            firstComplete.ShareId != secondComplete.ShareId && firstComplete.BytesTransferred > 0 && secondComplete.BytesTransferred > 0,
            unauthorized, storeReopened, overlapRejected, idle, followed, stoppedAtTerminal, cleared, cancelled, alerted));

        WebApplication Startable(LibraryHostOptions options)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        }

        static string PlaylistPath(string share) => Path.GetRelativePath(share, Assert.Single(Directory.EnumerateFiles(share, "*.m3u8", SearchOption.AllDirectories)))
            .Replace('\\', '/');
        static T DesktopValue<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    [Fact]
    public async Task HttpsAuthorizesTheLibraryAndRecoversARealCommittedReceiptAfterResponseLoss()
    {
        var hostPath = Path.Combine(root, "host.db");
        SeedHost(hostPath);
        using var hostStore = LiteDbHostPlaylistStore.Open(hostPath, false);
        using var certificate = CreateCertificate();
        await using var host = BuildHost(hostStore, certificate);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());

        using var transport = new LostResponseHandler(CreateClientHandler(certificate));
        using var client = new HttpClient(transport) { BaseAddress = address };

        var track = Guid.NewGuid();
        var create = new PlaylistSaveRequest(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
            "Original", [new(Guid.NewGuid(), track), new(Guid.NewGuid(), track)]);
        using var unauthorized = await PostAsync(client, library, create, credential: null);
        using var invalidToken = await PostAsync(client, library, create, credential: "test-unknown-token");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidToken.StatusCode);
        using var impersonating = await PostAsync(client, library, create with { OriginInstanceId = Guid.NewGuid() }, secret);
        using var outOfScope = await PostAsync(client, Guid.NewGuid(), create, secret);
        using var createdResponse = await PostAsync(client, library, create, secret);
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token);
        Assert.NotNull(created);
        using var replay = await PostAsync(client, library, create, secret);
        var replayed = await replay.Content.ReadFromJsonAsync<PlaylistSaveResponse>(Token);
        Assert.Equal(created, replayed);
        using var stale = await PostAsync(client, library, create with
        {
            PlaylistId = created.PlaylistId, ExpectedRevision = 99, OperationId = Guid.NewGuid()
        }, secret);
        PlaylistHttpAssertions.Verify(unauthorized.StatusCode, impersonating.StatusCode, outOfScope.StatusCode, replay.StatusCode, stale.StatusCode);

        var desktopPath = Path.Combine(root, "desktop.db");
        SeedDesktop(desktopPath, created, create);
        using var desktop = LiteDbDesktopPlaylistStore.Open(desktopPath, false);
        var catalog = new LiteDbCatalogReader(desktop, library, root, () => false);
        var gateway = new HttpPlaylistPublicationHost(client, address, library, new TokenProvider(library, instance, secret));
        var state = new PlaybackState(playback, 0, new(0, [], true), PlaybackCapabilities.None, TransportStatus.Stopped);
        var owner = new PlaybackSessionHandlers(state, DeckId.A, new UnusedAudio(),
            queuePlaylists: new QueuePlaylistPublication(catalog, gateway, desktop, instance));
        var dispatcher = owner.Register(new RequestDispatcher.Builder()).Build();
        var request = new RequestContext(Guid.NewGuid(), instance);
        var queue = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(
            new LoadPlaylistQueue(playback, created.PlaylistId, 1), new(request, Guid.NewGuid()), Token)).Value.Queue;
        var reversed = queue.Entries.Reverse().Select(entry => entry.EntryId).ToArray();
        transport.DropNextSuccess = true;
        var operation = Guid.NewGuid();
        var outstanding = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(new EditQueue(playback,
            new QueueEdit.Reorder([.. reversed]), queue.QueueRevision, 1), new(request, operation), Token)).Value.Queue;
        Assert.Equal(QueueSaveStatus.Pending, outstanding.SaveStatus);
        var committed = Assert.IsType<HostOutcome<SavedHostPlaylist?>.Success>(await hostStore.ReadPlaylistAsync(library, created.PlaylistId, Token)).Value!;
        Assert.Equal(reversed, committed.Entries.Select(entry => entry.EntryId));
        Assert.Equal(2, committed.Revision);

        var recovered = Assert.IsType<Outcome<PlaybackState>.Success>(await owner.ReplayQueuePlaylistSavesAsync(playback, request, Token)).Value.Queue;
        Assert.Equal(QueueSaveStatus.Saved, recovered.SaveStatus);
        Assert.Equal(playback, recovered.SaveReceipt?.QueueSessionId);
        Assert.Equal(2, recovered.SaveReceipt?.Revision);
        var publication = Assert.IsType<HostOutcome<HostPlaylistPublication>.Success>(await hostStore.ReadPublicationAsync(library, 2, Token)).Value;
        Assert.Equal(2, publication.CurrentRevision);
        Assert.Equal(new HostOperationKey(instance, operation), publication.Origin);
    }

    /// <summary>Verifies device staging scope, CQRS mapping and stable replay through actual HTTPS.</summary>
    /// <remarks>2026-11-24: Bind the mock transport assertions for TEST-FILESYNC-001/002.</remarks>
    [Fact]
    public async Task DeviceStagePreservesTheSelectedDeviceAndRegisteredOriginThroughDesktopDispatch()
    {
        var hostPath = Path.Combine(root, "host.db");
        var device = Guid.NewGuid();
        var receiver = new RegisteredAndroidExport(library, device, 1, "android-export", "fixture-receiver", new("https://receiver.test/"), false);
        SeedHost(hostPath, [receiver]);
        using var hostStore = LiteDbHostPlaylistStore.Open(hostPath, false);
        using var certificate = CreateCertificate();
        var stage = new HostDevicePlaylistStager(hostStore, new DeviceInventory(receiver), new([new M3u8PlaylistFormatter()]),
            new FileStagedPlaylistContent(Path.Combine(root, "staged")));
        await using var host = BuildHost(hostStore, certificate, stage);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var transport = new LostResponseHandler(CreateClientHandler(certificate));
        using var client = new HttpClient(transport) { BaseAddress = address };

        var track = Guid.NewGuid();
        var playlist = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await new HostPlaylistWriter(hostStore).SaveAsync(library,
            new(library, instance), new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                "Ready", [new(Guid.NewGuid(), track)]), Token)).Value;
        var operation = Guid.NewGuid();
        var stageRequest = new StageDevicePlaylistRequest(instance, operation, playlist.PlaylistId, device, "musicolet", 2, null, null);
        using var missing = await PostStageAsync(client, library, stageRequest, null);
        using var wrongLibrary = await PostStageAsync(client, Guid.NewGuid(), stageRequest, secret);
        using var impersonating = await PostStageAsync(client, library, stageRequest with { OriginInstanceId = Guid.NewGuid() }, secret);
        Assert.Equal(HttpStatusCode.Forbidden, impersonating.StatusCode);

        var gateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand(new DesktopSync.StagePlaylistForDeviceHandler(gateway)).Build();
        var command = new DesktopSync.StagePlaylistForDevice(playlist.PlaylistId, device, "musicolet", 2, null, null);
        var context = new CommandContext(new(Guid.NewGuid(), instance), operation);
        transport.DropNextSuccess = true;
        var lost = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.DependencyFailure>(await dispatcher.SendAsync(command, context, Token));
        Assert.True(lost.Retryable);
        var plan = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(await dispatcher.SendAsync(command, context, Token)).Value;
        Assert.Equal(track, Assert.Single(plan.Entries.OfType<DesktopSync.ExportEntry.Audio>()).TrackId);
        Assert.Equal(track, Assert.Single(Assert.Single(plan.Entries.OfType<DesktopSync.ExportEntry.Playlist>()).Tracks).TrackId);
        var changed = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Conflict>(
            await dispatcher.SendAsync(command with { DeviceId = Guid.NewGuid() }, context, Token));
        Assert.Equal("operation-intent-mismatch", changed.Code);

        using var replay = await PostStageAsync(client, library, stageRequest, secret);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await replay.Content.ReadFromJsonAsync<StageDevicePlaylistResponse>(Token);
        Assert.NotNull(result);
        using var wrongIntent = await PostStageAsync(client, library, stageRequest with { DeviceId = Guid.NewGuid() }, secret);
        StageAssertions.VerifyTransport(new((int)missing.StatusCode, (int)wrongLibrary.StatusCode, result.OperationId, result.Plan.DeviceId,
            result.Plan.ProfileId, result.Plan.ProfileRevision, result.Plan.PlanRevision!.Value, (int)wrongIntent.StatusCode), operation, device);
        Assert.Equal(plan.PlanId, result.Plan.PlanId);
        Assert.Equal(plan.Entries.Select(entry => entry.EntryId), result.Plan.Entries.Select(entry => entry.EntryId));
    }

    /// <summary>Proves the desktop can restore the exact staged scope through authenticated HTTPS after reopening LiteDB.</summary>
    /// <remarks>2026-11-24: Bind TEST-FILESYNC-001/002 for scoped device plan reload and profile selection.</remarks>
    [Fact]
    public async Task DeviceQueriesReloadTheSelectedPlanWithoutExposingAnotherScope()
    {
        var databasePath = Path.Combine(root, "host.db");
        var receiver = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "receiver-a", new("https://receiver.test/"), false);
        var otherDevice = receiver with { DeviceId = Guid.NewGuid(), ShareId = "android-b", SyncthingDeviceId = "receiver-b" };
        var revoked = Guid.NewGuid();
        SeedHost(databasePath, [receiver, otherDevice], revoked);
        StageDevicePlaylistResponse staged;
        using (var original = LiteDbHostPlaylistStore.Open(databasePath, false))
        {
            var caller = new AuthenticatedHostInstance(library, instance);
            var playlist = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await new HostPlaylistWriter(original).SaveAsync(library,
                caller, new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                    "Ready", [new(Guid.NewGuid(), Guid.NewGuid())]), Token)).Value;
            var stager = new HostDevicePlaylistStager(original, new DeviceInventory(receiver), new([new M3u8PlaylistFormatter()]),
                new FileStagedPlaylistContent(Path.Combine(root, "staged")));
            staged = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Success>(await stager.StageAsync(library, caller,
                new(instance, Guid.NewGuid(), playlist.PlaylistId, receiver.DeviceId, "musicolet", 2, null, null), Token)).Value;
        }

        using var reopened = LiteDbHostPlaylistStore.Open(databasePath, false);
        using var certificate = CreateCertificate();
        var reader = new HostDeviceExportReader(reopened, new AndroidLayoutProfiles([new M3u8PlaylistFormatter()]).LoadBuiltIn());
        await using var host = BuildHost(reopened, certificate, reader: reader, revokedInstance: revoked);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var client = new HttpClient(CreateClientHandler(certificate));

        var gateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
        var queries = new DesktopSync.DeviceExportQueryHandlers(gateway);
        var dispatcher = new RequestDispatcher.Builder().RegisterQuery<DesktopSync.GetDeviceLayouts, DesktopSync.DeviceLayoutsSnapshot>(queries)
            .RegisterQuery<DesktopSync.GetDeviceExportPlan, DesktopSync.DeviceExportPlan>(queries).Build();
        var context = new RequestContext(Guid.NewGuid(), instance);
        var layouts = Assert.IsType<Outcome<DesktopSync.DeviceLayoutsSnapshot>.Success>(
            await dispatcher.QueryAsync(new DesktopSync.GetDeviceLayouts(receiver.DeviceId), context, Token)).Value;
        Assert.All(layouts.Profiles, profile => Assert.Equal("/storage/emulated/0/Music/Beets", profile.SuggestedRoot));
        var reopenedPlan = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(await dispatcher.QueryAsync(
            new DesktopSync.GetDeviceExportPlan(receiver.DeviceId, ProfileId: "musicolet", ProfileRevision: 2), context, Token)).Value;
        var alternate = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(await dispatcher.QueryAsync(
            new DesktopSync.GetDeviceExportPlan(receiver.DeviceId, ProfileId: "poweramp", ProfileRevision: 3), context, Token)).Value;
        Assert.Equal("poweramp", alternate.ProfileId);
        Assert.Equal(3, alternate.ProfileRevision);

        var wrongDevice = Invalid(await dispatcher.QueryAsync(new DesktopSync.GetDeviceExportPlan(otherDevice.DeviceId, staged.Plan.PlanId), context, Token));
        var wrongProfile = Invalid(await dispatcher.QueryAsync(new DesktopSync.GetDeviceExportPlan(receiver.DeviceId, staged.Plan.PlanId,
            "poweramp", 3), context, Token));
        var legacy = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(await dispatcher.QueryAsync(
            new DesktopSync.GetDeviceExportPlan(receiver.DeviceId, staged.Plan.PlanId), context, Token)).Value;
        Assert.Equal(staged.Plan.PlanId, legacy.PlanId);
        var currentDefault = Assert.IsType<Outcome<DesktopSync.DeviceExportPlan>.Success>(await dispatcher.QueryAsync(
            new DesktopSync.GetDeviceExportPlan(receiver.DeviceId), context, Token)).Value;
        Assert.Equal(staged.Plan.PlanId, currentDefault.PlanId);

        var revokedGateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, revoked, RevokedCredential));
        var revokedOutcome = await revokedGateway.ReadPlanAsync(new(receiver.DeviceId, staged.Plan.PlanId), new(Guid.NewGuid(), revoked), Token);
        StageAssertions.VerifyDeviceExportRead(new(reopenedPlan.PlanId, reopenedPlan.PlanRevision, [.. reopenedPlan.Entries.Select(item => item.EntryId)],
            [.. layouts.Profiles.Select(profile => profile.ProfileId)], alternate.PlanId is null && alternate.PlanRevision is null && alternate.Entries.IsEmpty,
            wrongDevice, wrongProfile, Invalid(revokedOutcome)), staged.Plan.PlanId!.Value, [.. staged.Plan.Entries.Select(item => item.EntryId)]);
    }

    /// <summary>Checks the desktop commands return one reviewed publication even when both HTTPS receipts are lost.</summary>
    /// <remarks>2026-11-24: Bind authorized review, stale rejection and replay assertions to TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public async Task DevicePreviewAndPublishRecoverTheExactReceiptsThroughDesktopDispatch()
    {
        var databasePath = Path.Combine(root, "host.db");
        var receiver = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-preview", "receiver-preview", new("https://receiver.test/"), false);
        var other = receiver with { DeviceId = Guid.NewGuid(), ShareId = "another-share", SyncthingDeviceId = "other-receiver" };
        SeedHost(databasePath, [receiver, other]);
        var track = Guid.NewGuid();
        using (var fixture = new LiteDatabase(databasePath))
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
                { ["_id"] = track, ["LibraryId"] = library, ["RelativePath"] = "Missing.flac" });
        var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var share = Directory.CreateDirectory(Path.Combine(root, "share")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "Missing.flac"), "reviewed audio", Token);
        var payloads = new FileStagedPlaylistContent(Path.Combine(root, "stage"));
        using var store = LiteDbHostPlaylistStore.Open(databasePath, false);
        var caller = new AuthenticatedHostInstance(library, instance);
        var playlist = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await new HostPlaylistWriter(store).SaveAsync(library,
            caller, new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Ready",
                [new(Guid.NewGuid(), track)]), Token)).Value;
        var stage = new HostDevicePlaylistStager(store, new DeviceInventory(receiver), new([new M3u8PlaylistFormatter()]), payloads);
        var staged = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Success>(await stage.StageAsync(library, caller,
            new(instance, Guid.NewGuid(), playlist.PlaylistId, receiver.DeviceId, "musicolet", 2, null, null), Token)).Value.Plan;
        var publisher = new HostDeviceExportPublisher(store, store, new FileDeviceExportStepWriter(share, work, receiver, payloads));
        var workflow = new StagedDeviceExportWorkflow(store, store, store, new FileDeviceExportSnapshotReader(share, receiver), publisher, payloads, media);
        using var certificate = CreateCertificate();
        await using var host = BuildHost(store, certificate, export: workflow);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var transport = new LostResponseHandler(CreateClientHandler(certificate));
        using var client = new HttpClient(transport) { BaseAddress = address };
        var gateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
        var dispatcher = new DesktopSync.DeviceExportCommandHandlers(gateway).Register(new RequestDispatcher.Builder())
            .Build(typeof(DesktopSync.PrepareDeviceExportPreview), typeof(DesktopSync.PublishDeviceExport));
        var request = new RequestContext(Guid.NewGuid(), instance);
        var context = new CommandContext(request, Guid.NewGuid());
        var prepare = new DesktopSync.PrepareDeviceExportPreview(receiver.DeviceId, "musicolet", 2, staged.PlanId, staged.PlanRevision);
        var wire = new PrepareDeviceExportRequest(instance, context.OperationId, receiver.DeviceId, "musicolet", 2, staged.PlanId, staged.PlanRevision, null);
        using var missing = await PostPreviewAsync(library, wire, credential: null);
        using var outside = await PostPreviewAsync(Guid.NewGuid(), wire, secret);
        using var spoofed = await PostPreviewAsync(library, wire with { OriginInstanceId = Guid.NewGuid() }, secret);
        var wrongDevice = Invalid(await dispatcher.SendAsync(prepare with { DeviceId = other.DeviceId }, new(request, Guid.NewGuid()), Token));
        var wrongProfile = Invalid(await dispatcher.SendAsync(prepare with { ProfileId = "poweramp", ProfileRevision = 3 }, new(request, Guid.NewGuid()), Token));
        Assert.Equal(wrongDevice, wrongProfile);

        transport.DropNextSuccess = true;
        var firstPrepare = await dispatcher.SendAsync(prepare, context, Token);
        var preview = Assert.IsType<Outcome<DesktopSync.DeviceExportPreview>.Success>(await dispatcher.SendAsync(prepare, context, Token)).Value;
        Assert.Empty(preview.BlockingIssues);
        var shareEmpty = !Directory.EnumerateFileSystemEntries(share).Any();
        var id = preview.Plan.PlanId!.Value;
        var revision = preview.Plan.PlanRevision!.Value;
        var stale = Assert.IsType<Outcome<DesktopSync.DeviceExportSubmission>.Conflict>(await dispatcher.SendAsync(
            new DesktopSync.PublishDeviceExport(id, staged.PlanRevision!.Value), new(request, Guid.NewGuid()), Token));
        var publish = new DesktopSync.PublishDeviceExport(id, revision);
        var operation = new CommandContext(request, Guid.NewGuid());
        transport.DropNextSuccess = true;
        var firstPublish = await dispatcher.SendAsync(publish, operation, Token);
        var published = Published(await dispatcher.SendAsync(publish, operation, Token));
        var replay = Published(await dispatcher.SendAsync(publish, operation, Token));
        var manifest = Assert.IsType<HostOutcome<PublishedDeviceExportManifest>.Success>(
            await store.ReadPublishedManifestAsync(library, caller, receiver.DeviceId, Token)).Value;
        StageAssertions.VerifyDeviceExportCommands(new((int)missing.StatusCode, (int)outside.StatusCode, (int)spoofed.StatusCode, wrongDevice, revision,
            [.. preview.Changes.Select(change => $"{change.Kind}:{change.After}")], shareEmpty, stale.Code,
            firstPrepare is Outcome<DesktopSync.DeviceExportPreview>.DependencyFailure { Code: "export-preview-host-unreachable", Retryable: true },
            firstPublish is Outcome<DesktopSync.DeviceExportSubmission>.DependencyFailure { Code: "export-publish-host-unreachable", Retryable: true },
            published.PublicationId, published.TransferId, published == replay, manifest.Revision));
        Assert.Equal("reviewed audio", await File.ReadAllTextAsync(Path.Combine(share, "Music", "Missing.flac"), Token));
        Assert.Equal("#EXTM3U\n../Music/Missing.flac\n", await File.ReadAllTextAsync(Path.Combine(share, "Playlists", "Ready.m3u8"), Token));

        async Task<HttpResponseMessage> PostPreviewAsync(Guid target, PrepareDeviceExportRequest body, string? credential)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, DeviceExportCommandRoutes.PreparePath(target)) { Content = JsonContent.Create(body) };
            if (credential is not null)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return await client.SendAsync(message, Token);
        }

        static DesktopSync.ExportPublicationReceipt Published(Outcome<DesktopSync.DeviceExportSubmission> result) =>
            Assert.IsType<DesktopSync.DeviceExportSubmission.Published>(Assert.IsType<Outcome<DesktopSync.DeviceExportSubmission>.Success>(result).Value).Receipt;
    }

    /// <summary>Reads durable receiver progress and retries one checkpoint through the desktop commands and actual HTTPS.</summary>
    /// <remarks>2026-11-24: Bind scoped inspection, lost retry receipt and verified progress for TEST-FILESYNC-001/002.</remarks>
    [Fact]
    public async Task DesktopInspectsAndRetriesTheSamePublishedTransferOverHttps()
    {
        var databasePath = Path.Combine(root, "host.db");
        var receiver = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-progress", "receiver-progress", new("https://receiver.test/"), false);
        SeedHost(databasePath, [receiver]);
        using var store = LiteDbHostPlaylistStore.Open(databasePath, false);
        var caller = new AuthenticatedHostInstance(library, instance);
        var track = Guid.NewGuid();
        var saved = HostValue(await new HostPlaylistWriter(store).SaveAsync(library, caller,
            new(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Ready", [new(Guid.NewGuid(), track)]), Token));
        var payloads = new FileStagedPlaylistContent(Path.Combine(root, "transfer-stage"));
        var stager = new HostDevicePlaylistStager(store, new DeviceInventory(receiver), new([new M3u8PlaylistFormatter()]), payloads);
        var staged = HostValue(await stager.StageAsync(library, caller,
            new(instance, Guid.NewGuid(), saved.PlaylistId, receiver.DeviceId, "musicolet", 2, null, null), Token)).Plan;
        var share = Directory.CreateDirectory(Path.Combine(root, "share")).FullName;
        var review = HostValue(new DeviceExportPublicationPlanner().Prepare(staged, receiver,
            HostValue(await store.ReadPublishedManifestAsync(library, caller, receiver.DeviceId, Token)), new(library, receiver.DeviceId, receiver.ShareId, []),
            DeviceExportPublicationMode.AddToShare,
            [new(new(ExportContentKind.Audio, track), "Music/Missing.flac", true, true, await StoreAsync("data")),
                new(new(ExportContentKind.Playlist, saved.PlaylistId), "Playlists/Ready.m3u8", true, true, await StoreAsync("ready"))]));
        var work = Directory.CreateDirectory(Path.Combine(root, "transfer-work")).FullName;
        var publication = new DeviceExportPublicationIntent(Guid.NewGuid(), staged.PlanId!.Value, staged.PlanRevision!.Value);
        Assert.IsType<DeviceExportPublicationReservation.Pending>(HostValue(await store.ReservePublicationAsync(library, caller, publication, review, Token)));
        var publisher = new HostDeviceExportPublisher(store, store, new FileDeviceExportStepWriter(share, work, receiver, payloads));
        var published = HostValue(await publisher.ResumeAsync(library, caller, publication, Token));
        var transfer = published.TransferId!.Value;
        var clock = new TransferClock(DateTimeOffset.UtcNow);
        var scan = new TransferSender();
        var dispatchScan = new DeviceExportScanDispatcher(store, scan, clock);
        Assert.True(HostValue(await dispatchScan.RunNextAsync(Token)));
        Assert.False(HostValue(await dispatchScan.RunNextAsync(Token)));

        using var certificate = CreateCertificate();
        await using var host = BuildHost(store, certificate, transferClock: clock);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var transport = new LostResponseHandler(CreateClientHandler(certificate));
        using var client = new HttpClient(transport) { BaseAddress = address };
        using var unauthorized = await client.GetAsync(DeviceExportTransferRoutes.ReadPath(library, transfer), Token);
        using var outsideRequest = new HttpRequestMessage(HttpMethod.Get, DeviceExportTransferRoutes.ReadPath(Guid.NewGuid(), transfer));
        outsideRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var outside = await client.SendAsync(outsideRequest, Token);

        var gateway = new HttpDevicePlaylistExportHost(client, address, library, new TokenProvider(library, instance, secret));
        var dispatcher = new DesktopSync.DeviceExportTransferHandlers(gateway).Register(new RequestDispatcher.Builder())
            .Build(typeof(DesktopSync.GetMediaTransfer), typeof(DesktopSync.ResumeMediaTransfer));
        var context = new RequestContext(Guid.NewGuid(), instance);
        var failed = await InspectAsync();
        var stale = Assert.IsType<Outcome<DesktopSync.MediaTransferState>.Conflict>(await dispatcher.SendAsync(
            new DesktopSync.ResumeMediaTransfer(transfer, failed.Revision - 1), new(context, Guid.NewGuid()), Token));
        Assert.Equal(failed.Revision, stale.ActualRevision);
        var retry = new DesktopSync.ResumeMediaTransfer(transfer, failed.Revision);
        var retryOperation = new CommandContext(context, Guid.NewGuid());
        transport.DropNextSuccess = true;
        var lost = await dispatcher.SendAsync(retry, retryOperation, Token);
        var accepted = TransferValue(await dispatcher.SendAsync(retry, retryOperation, Token));
        Assert.Null(accepted.FailureCode);
        Assert.True(HostValue(await dispatchScan.RunNextAsync(Token)));
        Assert.Equal(2, scan.Calls);

        var verifier = new TransferObserver();
        var delivery = new DeviceExportReceiverDispatcher(store, verifier, clock);
        Assert.True(HostValue(await delivery.RunNextAsync(Token)));
        var partial = await InspectAsync();

        clock.Advance(TimeSpan.FromSeconds(11));
        verifier.FailOnce = true;
        Assert.True(HostValue(await delivery.RunNextAsync(Token)));
        var presentation = new PresentationServices(dispatcher, new InlineScheduler(), instance);
        using var shell = new ShellViewModel(presentation, Guid.NewGuid());
        using var media = new MediaSyncViewModel(presentation, Guid.NewGuid(), shell);
        await media.InspectTransferCommand.ExecuteAsync(transfer);
        Assert.True(media.RetryTransferCommand.CanExecute(null));
        var beforeViewRetry = Assert.IsType<DesktopSync.MediaTransferState>(media.Transfer);
        transport.DropNextSuccess = true;
        await media.RetryTransferCommand.ExecuteAsync(null);
        Assert.Equal("export-transfer-retry-host-unreachable", media.Failure?.Code);
        await media.RetryTransferCommand.ExecuteAsync(null);
        var viewReplaySucceeded = media.Transfer?.Revision == beforeViewRetry.Revision + 1 && media.Failure is null;

        verifier.AllArrived = true;
        Assert.True(HostValue(await delivery.RunNextAsync(Token)));
        var complete = await InspectAsync();
        var replay = TransferValue(await dispatcher.SendAsync(retry, retryOperation, Token));
        Assert.Equal(complete, await InspectAsync());
        await media.InspectTransferCommand.ExecuteAsync(transfer);
        await media.ReceiveTransferAsync(replay);
        Assert.Equal(complete, media.Transfer);
        var original = Assert.IsType<DeviceExportPublicationReservation.Completed>(HostValue(await store.FindPublicationAsync(library, caller, publication, Token)));
        var samePublication = original.Receipt == published && HostValue(await store.ReadPublishedManifestAsync(library, caller, receiver.DeviceId, Token)).Revision == 1 &&
            await File.ReadAllTextAsync(Path.Combine(share, "Music", "Missing.flac"), Token) == "data";
        var expectedShare = new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"device-export-share/v1/{library:N}/{receiver.DeviceId:N}/android-progress"))[..16]);
        StageAssertions.VerifyExportTransferTransport(new(complete.TransferId, complete.ShareId, (int)unauthorized.StatusCode, (int)outside.StatusCode,
            failed.Phase.ToString(), failed.Retryable, failed.FailureCode!, stale.Code,
            lost is Outcome<DesktopSync.MediaTransferState>.DependencyFailure { Code: "export-transfer-retry-host-unreachable", Retryable: true },
            accepted == replay && viewReplaySucceeded, accepted.Revision - failed.Revision, partial.Phase.ToString(), partial.BytesTransferred,
            complete.Phase.ToString(), complete.BytesTransferred, !complete.Retryable && !media.RetryTransferCommand.CanExecute(null), samePublication), transfer, expectedShare);

        Task<string> StoreAsync(string content) => payloads.StoreAsync([.. System.Text.Encoding.UTF8.GetBytes(content)], Token);
        async Task<DesktopSync.MediaTransferState> InspectAsync() => TransferValue(await dispatcher.QueryAsync(new DesktopSync.GetMediaTransfer(transfer), context, Token));
        static DesktopSync.MediaTransferState TransferValue(Outcome<DesktopSync.MediaTransferState> outcome) =>
            Assert.IsType<Outcome<DesktopSync.MediaTransferState>.Success>(outcome).Value;
        static T HostValue<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;
    }

    private static string Invalid<T>(Outcome<T> outcome) => Assert.Single(Assert.IsType<Outcome<T>.Invalid>(outcome).Issues).Code;

    private const string RevokedCredential = "fixture-host-revoked-instance";

    private WebApplication BuildHost(LiteDbHostPlaylistStore store, X509Certificate2 certificate, HostDevicePlaylistStager? stage = null,
        HostDeviceExportReader? reader = null, Guid? revokedInstance = null, StagedDeviceExportWorkflow? export = null, TimeProvider? transferClock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        builder.Services.AddSingleton(new HostPlaylistWriter(store));
        builder.Services.AddSingleton<IHostAccessTokenVerifier>(new Verifier(new(library, instance), secret, revokedInstance));
        if (stage is not null)
            builder.Services.AddSingleton(stage);
        if (reader is not null)
            builder.Services.AddSingleton(reader);
        if (export is not null)
            builder.Services.AddSingleton(export);
        if (transferClock is not null)
        {
            builder.Services.AddSingleton(transferClock);
            builder.Services.AddSingleton<IDeviceExportTransferStore>(store);
        }
        var host = builder.Build();
        host.MapPlaylistPublication();
        if (stage is not null)
            host.MapDevicePlaylistStage();
        if (reader is not null)
            host.MapDeviceExportQueries();
        if (export is not null)
            host.MapDeviceExportCommands();
        if (transferClock is not null)
            host.MapDeviceExportTransfers();
        return host;
    }

    /// <summary>Creates a caller-owned loopback certificate for the host HTTPS fixtures.</summary>
    /// <remarks>2026-11-26: Supply a Schannel-compatible key for TEST-BEETS-002 and TEST-RECORDS-002, preserving the exact certificate pin.</remarks>
    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        if (!OperatingSystem.IsWindows())
            return certificate;

        using (certificate)
        {
            var bundle = certificate.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(bundle, null, X509KeyStorageFlags.UserKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle);
            }
        }
    }

    private static HttpClientHandler CreateClientHandler(X509Certificate2 certificate) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, remote, _, errors) =>
            remote is not null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
            CryptographicOperations.FixedTimeEquals(remote.GetCertHash(HashAlgorithmName.SHA256), certificate.GetCertHash(HashAlgorithmName.SHA256))
    };

    private async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid target, PlaylistSaveRequest request, string? credential)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, PlaylistPublicationRoute.Path(target)) { Content = JsonContent.Create(request) };
        if (credential is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(message, Token);
    }

    private async Task<HttpResponseMessage> PostStageAsync(HttpClient client, Guid target, StageDevicePlaylistRequest request, string? credential)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, DevicePlaylistStageRoute.Path(target)) { Content = JsonContent.Create(request) };
        if (credential is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(message, Token);
    }

    private void SeedHost(string path, IReadOnlyList<RegisteredAndroidExport>? receivers = null, Guid? revokedInstance = null)
    {
        using (LiteDbHostPlaylistStore.Open(path, true)) { }
        using var fixture = new LiteDatabase(path);
        var registered = fixture.GetCollection<BsonDocument>("registered_instances");
        registered.Insert(new BsonDocument
        {
            ["_id"] = instance, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active"
        });
        foreach (var receiver in receivers ?? [])
            TestAndroidRegistration.Seed(fixture, receiver);
        if (revokedInstance is { } revoked)
            registered.Insert(new BsonDocument { ["_id"] = revoked, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Revoked" });
    }

    private void SeedDesktop(string path, PlaylistSaveResponse accepted, PlaylistSaveRequest create)
    {
        using (LiteDbDesktopPlaylistStore.Open(path, true)) { }
        using var fixture = new LiteDatabase(path);
        fixture.GetCollection<BsonDocument>("catalog_playlists").Insert(new BsonDocument
        {
            ["_id"] = accepted.PlaylistId, ["LibraryId"] = library, ["PlaylistRevision"] = accepted.PlaylistRevision,
            ["SourceRevision"] = accepted.FeedRevision, ["Name"] = create.Name,
            ["Entries"] = new BsonArray(create.Entries.Select(entry => (BsonValue)new BsonDocument { ["EntryId"] = entry.EntryId, ["TrackId"] = entry.TrackId }))
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class ExportInterruption
    {
        public bool Armed { get; set; }
        public int Calls { get; set; }
    }

    private sealed class InterruptedExportWriter(ExportInterruption settings, Func<PendingDeviceExportPublication, IDeviceExportStepWriter> writer)
        : IDeviceExportStepWriter
    {
        public Task<HostOutcome<bool>> ApplyAsync(PendingDeviceExportPublication work, int ordinal, CancellationToken token)
        {
            if (settings.Armed && ordinal == 1)
            {
                settings.Armed = false;
                settings.Calls++;
                return Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Unavailable("export-test-interrupted"));
            }
            return writer(work).ApplyAsync(work, ordinal, token);
        }
    }

    private sealed class FixtureHostVault(IReadOnlyDictionary<string, string> secrets) : ILocalHostCredentialVault
    {
        public ConcurrentQueue<string> Requested { get; } = new();

        public ValueTask<string?> ReadAsync(string reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requested.Enqueue(reference);
            return ValueTask.FromResult(secrets.GetValueOrDefault(reference));
        }
    }

    private sealed class Verifier(AuthenticatedHostInstance identity, string expected, Guid? revokedInstance) : IHostAccessTokenVerifier
    {
        public ValueTask<AuthenticatedHostInstance?> VerifyAsync(string bearerToken, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthenticatedHostInstance?>(bearerToken == expected ? identity :
                bearerToken == RevokedCredential && revokedInstance is { } revoked ? identity with { InstanceId = revoked } : null);
    }

    private sealed class TokenProvider(Guid library, Guid instance, string token) : IRegisteredHostTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid instanceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(libraryId == library && instanceId == instance ? token : null);
    }

    private sealed class DeviceInventory(RegisteredAndroidExport receiver) : IHostDevicePlaylistResolver
    {
        public Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token) =>
            Task.FromResult<HostOutcome<ResolvedDevicePlaylist>>(new HostOutcome<ResolvedDevicePlaylist>.Success(new(receiver,
                new(playlist.PlaylistId, request.DeviceId, "musicolet", 2, "m3u8", "Playlists/Ready.m3u8", false,
                    [.. playlist.Entries.Select(entry => new ResolvedPlaylistTrack(entry.TrackId, "Music/Missing.flac", false, true))]))));
    }

    private sealed class LostResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool DropNextSuccess { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (DropNextSuccess && response.IsSuccessStatusCode)
            {
                DropNextSuccess = false;
                response.Dispose();
                throw new HttpRequestException("The test discarded the committed response.");
            }
            return response;
        }
    }

    private sealed class TransferClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class StartupSyncthingCredentials(Guid expectedLibrary, Guid first, Guid second) :
        IRegisteredSyncthingTokenProvider, IHostSenderTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) => ValueTask.FromResult<string?>(
            libraryId != expectedLibrary ? null : deviceId == first ? "fixture-sync-android-a" : deviceId == second ? "fixture-sync-android-b" : null);

        public ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token) => ValueTask.FromResult<string?>(
            libraryId == expectedLibrary && senderDeviceId == StartupSyncthingApi.SenderId ? "fixture-sync-sender" : null);
    }

    private sealed class StartupSyncthingApi(IReadOnlyDictionary<string, string> roots)
    {
        public const string SenderId = "fixture-startup-sender";
        public ConcurrentQueue<string> Scanned { get; } = new();

        public WebApplication Build()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapGet("/{peer}/rest/system/status", (string peer, HttpContext context) => Authorized(peer, context) && (peer == "sender" || roots.ContainsKey(peer))
                ? Results.Json(new { myID = peer == "sender" ? SenderId : peer }) : Results.Unauthorized());
            app.MapGet("/{peer}/rest/config/folders/{folder}", (string peer, string folder, HttpContext context) =>
                peer == "sender" && Authorized(peer, context) && roots.TryGetValue(folder, out var path)
                ? Results.Json(new { id = folder, path, type = "sendonly", paused = false,
                    devices = new[] { new { deviceID = SenderId }, new { deviceID = folder } } }) : Results.NotFound());
            app.MapGet("/{peer}/rest/db/status", (string peer, string folder, HttpContext context) => UsesFolder(peer, folder, context)
                ? Results.Json(new { state = "idle", ignorePatterns = false, sequence = 1, stateChanged = DateTimeOffset.UnixEpoch }) : Results.NotFound());
            app.MapGet("/{peer}/rest/db/file", (string peer, string folder, string file, HttpContext context) => ReadIndex(peer, folder, file, context));
            app.MapPost("/{peer}/rest/db/scan", (string peer, string folder, HttpContext context) =>
            {
                if (peer != "sender" || !UsesFolder(peer, folder, context))
                    return Results.NotFound();
                Scanned.Enqueue(folder);
                return Results.Ok();
            });
            return app;
        }

        private IResult ReadIndex(string peer, string folder, string file, HttpContext context)
        {
            if (!UsesFolder(peer, folder, context) || !Scanned.Contains(folder, StringComparer.Ordinal))
                return Results.Text("No such object in the index", "text/plain", statusCode: StatusCodes.Status404NotFound);
            if (Path.IsPathRooted(file) || file.Contains('\\') || file.Split('/').Any(part => part is "" or "." or ".."))
                return Results.BadRequest();
            var observed = new FileInfo(Path.Combine(roots[folder], file));
            if (!observed.Exists)
                return Results.Text("No such object in the index", "text/plain", statusCode: StatusCodes.Status404NotFound);
            return Results.Json(new { local = new { name = file, size = observed.Length, type = "FILE_INFO_TYPE_FILE", deleted = false,
                invalid = false, ignored = false, mustRescan = false, version = new[] { "START:1" } } });
        }

        private bool UsesFolder(string peer, string folder, HttpContext context) => roots.ContainsKey(folder) &&
            (peer == "sender" || peer == folder) && Authorized(peer, context);

        private static bool Authorized(string peer, HttpContext context) => context.Request.Headers.Authorization == $"Bearer fixture-sync-{peer}";
    }

    /// <summary>Observes actual transfer reads and holds one until workspace shutdown cancels it.</summary>
    /// <remarks>2026-11-25: Prove the lifetime and selected scope of FR-FILESYNC-001#ac-safety transfer reads.</remarks>
    private sealed class TransferReadTrackingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int reads;
        private int holdNext;
        internal int Reads => Volatile.Read(ref reads);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void HoldNextRead() => Interlocked.Exchange(ref holdNext, 1);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.Contains("/device-exports/transfers/", StringComparison.Ordinal) == true)
            {
                Interlocked.Increment(ref reads);
                if (Interlocked.Exchange(ref holdNext, 0) != 0)
                {
                    Entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        Cancelled.TrySetResult();
                        throw;
                    }
                }
            }
            return await base.SendAsync(request, token);
        }
    }

    private sealed class TransferSender : IDeviceExportSenderScanner
    {
        public int Calls { get; private set; }
        public Task<HostOutcome<bool>> ScanAsync(RegisteredAndroidExport target, CancellationToken token) => Task.FromResult<HostOutcome<bool>>(
            ++Calls == 1 ? new HostOutcome<bool>.Unavailable("export-scan-unavailable") : new HostOutcome<bool>.Success(true));
    }

    private sealed class TransferObserver : IDeviceExportReceiverVerifier
    {
        public bool AllArrived { get; set; }
        public bool FailOnce { get; set; }

        public Task<HostOutcome<ImmutableArray<DeviceExportTransferItem>>> CaptureSenderAsync(RegisteredAndroidExport target,
            ImmutableArray<DeviceExportTransferItem> items, CancellationToken token) => Task.FromResult<HostOutcome<ImmutableArray<DeviceExportTransferItem>>>(
            new HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Success([.. items.Select(item => item with
                { Bytes = item.Path.EndsWith(".flac", StringComparison.Ordinal) ? 4 : 5, Version = "SEND:1" })]));

        public Task<HostOutcome<DeviceExportReceiverProgress>> ObserveReceiverAsync(RegisteredAndroidExport target,
            ImmutableArray<DeviceExportTransferItem> items, CancellationToken token)
        {
            if (!FailOnce)
                return Task.FromResult<HostOutcome<DeviceExportReceiverProgress>>(new HostOutcome<DeviceExportReceiverProgress>.Success(new(AllArrived ? 9 : 4, 9, AllArrived)));
            FailOnce = false;
            return Task.FromResult<HostOutcome<DeviceExportReceiverProgress>>(new HostOutcome<DeviceExportReceiverProgress>.Unavailable("export-receiver-unavailable"));
        }
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            apply();
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedAudio : IPlaybackSeekTarget
    {
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Playlist publication does not use the audio backend.");
    }
}
