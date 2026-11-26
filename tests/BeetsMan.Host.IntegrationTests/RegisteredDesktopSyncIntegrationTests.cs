using System.Net;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using DesktopSync = BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Drops saved playlists through the registered Sync list command and checks both real formatter outputs.</summary>
    /// <remarks>2026-11-25: Exercise selected layouts, repeated drops and receiver inventory for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
    [Fact]
    public async Task RegisteredDesktopDropsPlaylistsInTheSelectedFormatWithOnlyMissingAudio()
    {
        var hostDatabase = Path.Combine(root, "desktop-sync-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var share = Directory.CreateDirectory(Path.Combine(root, "receiving")).FullName;
        var privateStage = Path.Combine(root, "private");
        var existing = Path.Combine(Directory.CreateDirectory(Path.Combine(share, "Unknown Artist", "Unknown Album")).FullName, "present.flac");
        await File.WriteAllTextAsync(existing, "present bytes", Token);
        var presentSource = Path.Combine(music, "present.flac");
        var missingSource = Path.Combine(music, "missing.flac");
        await File.WriteAllTextAsync(presentSource, "present bytes", Token);
        await File.WriteAllTextAsync(missingSource, "missing on receiver", Token);
        var layout = Path.Combine(root, "car-player.yaml");
        await File.WriteAllTextAsync(layout, """
            id: car-player
            player: Car Player
            templateVersion: 7
            suggestedRoot: /storage/emulated/0/Music/Beets
            audioPathTemplate: '{albumArtist}/{album}/{fileName}'
            playlistFormat: pls
            playlistPathTemplate: 'playlists/{playlistName}.{playlistFormat}'
            """, Token);
        var simulation = new StartupSyncthingApi(new Dictionary<string, string> { ["android-a"] = share });
        simulation.Scanned.Enqueue("android-a");
        await using var sync = simulation.Build();
        await sync.StartAsync(Token);
        var syncAddress = new Uri(sync.Urls.Single() + '/');
        var target = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android-a", "android-a", new(syncAddress, "android-a/"), false);
        SeedHost(hostDatabase, [target]);
        var presentTrack = Guid.NewGuid();
        var missingTrack = Guid.NewGuid();
        using (var db = new LiteDatabase(hostDatabase))
        {
            var tracks = db.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(new BsonDocument { ["_id"] = presentTrack, ["LibraryId"] = library, ["RelativePath"] = "present.flac" });
            tracks.Insert(new BsonDocument { ["_id"] = missingTrack, ["LibraryId"] = library, ["RelativePath"] = "missing.flac" });
        }
        Guid mixed, complete;
        using (var store = LiteDbHostPlaylistStore.Open(hostDatabase, false))
        {
            var writer = new HostPlaylistWriter(store);
            mixed = await SaveAsync("Mixed", presentTrack, missingTrack, presentTrack);
            complete = await SaveAsync("Complete", presentTrack, presentTrack);

            async Task<Guid> SaveAsync(string name, params Guid[] tracks)
            {
                var request = new PlaylistSaveRequest(instance, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null,
                    name, [.. tracks.Select(track => new PlaylistEntryData(Guid.NewGuid(), track))]);
                return Assert.IsType<HostOutcome<PlaylistSaveResponse>.Success>(await writer.SaveAsync(library, new(library, instance), request, Token)).Value.PlaylistId;
            }
        }

        using var certificate = CreateCertificate();
        var options = new LibraryHostOptions(library, hostDatabase, music, privateStage, Path.Combine(root, "work"),
            [new(target.DeviceId, target.ShareId, new(new(syncAddress, "sender/"), StartupSyncthingApi.SenderId, share))], AndroidLayoutFiles: [layout]);
        var credentials = new StartupSyncthingCredentials(library, target.DeviceId, Guid.Empty);
        var identity = new Verifier(new(library, instance), secret, null);
        var duplicate = Record.Exception(() => Build(options with { AndroidLayoutFiles = [layout, layout] }));
        await using var app = Build(options);
        await app.StartAsync(Token);
        var host = new Uri(app.Urls.Single());
        using var client = new HttpClient(CreateClientHandler(certificate));
        await using var desktop = new DesktopStartup(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "desktop"), music), [], library, instance,
            host: new(host, client, new TokenProvider(library, instance, secret)), presentationScheduler: new InlineScheduler());
        var started = Value(await desktop.StartAsync(Token));
        var routes = Assert.IsAssignableFrom<IRequestDispatcher>(started.Workspace);
        var caller = new RequestContext(Guid.NewGuid(), instance);
        var models = Assert.IsType<DesktopPresentation>(started.Presentation);
        var list = models.SyncList;
        var editor = models.DeviceExport;
        var disabled = list.Plan is null && !list.DropSavedPlaylistCommand.CanExecute(mixed);
        await models.Sync.LoadCommand.ExecuteAsync(target.DeviceId);
        var layouts = Assert.IsType<DesktopSync.DeviceLayoutsSnapshot>(editor.Layouts);
        var musicolet = layouts.Profiles.Single(profile => profile.ProfileId == "musicolet");
        var carPlayer = layouts.Profiles.Single(profile => profile.ProfileId == "car-player");
        var loaded = models.Sync.SelectedPeerId == target.DeviceId && layouts.DeviceId == target.DeviceId &&
            layouts.ActiveProfile == new DesktopSync.DeviceLayoutAssignment(musicolet.ProfileId, musicolet.Revision) &&
            list.Target == new DeviceExportTarget(target.DeviceId, musicolet.ProfileId, musicolet.Revision) &&
            list.Plan is { PlanId: null, Entries.IsEmpty: true } && list.DropSavedPlaylistCommand.CanExecute(mixed);
        await list.DropSavedPlaylistCommand.ExecuteAsync(mixed);
        Assert.Null(list.Failure);
        var m3u8 = Assert.IsType<DesktopSync.DeviceExportPlan>(list.Plan);
        var foreignProfile = carPlayer with { Revision = carPlayer.Revision + 1 };
        var rejectedProfile = !editor.SelectStagingProfileCommand.CanExecute(foreignProfile);
        await editor.SelectStagingProfileCommand.ExecuteAsync(carPlayer);
        var scoped = rejectedProfile && list.Target == new DeviceExportTarget(target.DeviceId, carPlayer.ProfileId, carPlayer.Revision) &&
            editor.Layouts?.ActiveProfile == layouts.ActiveProfile && list.Plan is { PlanId: null, Entries.IsEmpty: true };
        await list.DropSavedPlaylistCommand.ExecuteAsync(mixed);
        Assert.Null(list.Failure);
        var pls = Assert.IsType<DesktopSync.DeviceExportPlan>(list.Plan);
        await list.DropSavedPlaylistCommand.ExecuteAsync(mixed);
        Assert.Null(list.Failure);
        var repeated = Assert.IsType<DesktopSync.DeviceExportPlan>(list.Plan);
        await list.DropSavedPlaylistCommand.ExecuteAsync(complete);
        Assert.Null(list.Failure);
        var fullPlan = Assert.IsType<DesktopSync.DeviceExportPlan>(list.Plan);
        var added = fullPlan.Entries.Where(entry => repeated.Entries.All(previous => previous.EntryId != entry.EntryId));
        var reload = Value(await routes.QueryAsync(new DesktopSync.GetDeviceExportPlan(target.DeviceId, fullPlan.PlanId, carPlayer.ProfileId,
            carPlayer.Revision), caller, Token));
        var mixedM3u8 = Assert.Single(m3u8.Entries.OfType<DesktopSync.ExportEntry.Playlist>());
        var mixedPls = Assert.Single(pls.Entries.OfType<DesktopSync.ExportEntry.Playlist>());
        var texts = new List<string>();
        foreach (var path in Directory.EnumerateFiles(privateStage))
            texts.Add(await File.ReadAllTextAsync(path, Token));
        var m3u8Text = Assert.Single(texts, text => text.StartsWith("#EXTM3U", StringComparison.Ordinal));
        var plsText = Assert.Single(texts, text => text.StartsWith("[playlist]", StringComparison.Ordinal) && text.Contains("missing.flac", StringComparison.Ordinal));
        await editor.PreviewCommand.ExecuteAsync(new LayoutPreviewInput(carPlayer));
        var preview = Assert.IsType<DesktopSync.DeviceExportPreview>(editor.PreviewResult);
        var offered = preview.Plan.PlanId == fullPlan.PlanId && preview.Plan.ProfileId == carPlayer.ProfileId &&
            preview.BlockingIssues.IsEmpty && editor.CancelPreviewCommand.CanExecute(null);
        editor.CancelPreviewCommand.Execute(null);
        var preserved = offered && editor.PreviewResult is null && editor.Publication is null &&
            list.Plan?.PlanId == fullPlan.PlanId && editor.Layouts?.ActiveProfile == layouts.ActiveProfile;
        await models.Sync.LoadCommand.ExecuteAsync(Guid.NewGuid());
        var cleared = models.Sync.SelectedPeerId is null && list.Plan is null && editor.Layouts is null &&
            !list.DropSavedPlaylistCommand.CanExecute(mixed) && !editor.PublishCommand.CanExecute(null);
        await models.Sync.LoadCommand.ExecuteAsync(target.DeviceId);
        var restored = list.Plan?.PlanId == m3u8.PlanId && list.Plan?.ProfileId == musicolet.ProfileId &&
            list.Plan?.Entries.Select(entry => entry.EntryId).SequenceEqual(m3u8.Entries.Select(entry => entry.EntryId)) == true;
        Value(await desktop.StopAsync(Token));
        var stopped = !list.IsActive && !editor.IsActive;
        StageAssertions.VerifyComposedDrop(new([$"{m3u8.ProfileId}:{mixedM3u8.Format}", $"{pls.ProfileId}:{mixedPls.Format}"],
            [.. m3u8.Entries.Concat(pls.Entries).OfType<DesktopSync.ExportEntry.Audio>().Select(audio => Path.GetFileName(audio.Destination))],
            [.. mixedPls.Tracks.Select(track => Path.GetFileName(track.Destination))], m3u8Text, plsText,
            [.. added.Select(entry => Path.GetFileName(entry.Destination))],
            pls.Entries.Select(entry => entry.EntryId).SequenceEqual(repeated.Entries.Select(entry => entry.EntryId)) &&
                fullPlan.Entries.Select(entry => entry.EntryId).SequenceEqual(reload.Entries.Select(entry => entry.EntryId)),
            Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories).SequenceEqual([existing]) &&
                await File.ReadAllTextAsync(existing, Token) == "present bytes", duplicate is ArgumentException,
            disabled, loaded, scoped, preserved, cleared, restored, stopped));

        WebApplication Build(LibraryHostOptions configuration)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, configuration, identity, credentials, credentials);
        }
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }
}
