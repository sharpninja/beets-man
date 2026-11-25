using System.Text;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Verifies publication and interrupted replay on real, isolated filesystem paths and LiteDB.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-002 to the recoverable file effects of FR-FILESYNC-001#ac-safety.</remarks>
public sealed class FileDeviceExportPublicationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-export-publish-{Guid.NewGuid():N}");
    private readonly Guid origin = Guid.NewGuid();
    private readonly RegisteredAndroidExport receiver = new(Guid.NewGuid(), Guid.NewGuid(), 1, "export-share", "fixture-receiver",
        new("https://receiver.test/"), false);
    private string ExportRoot => Path.Combine(root, "export");
    private string Work => Path.Combine(root, "work");
    private string DatabasePath => Path.Combine(root, "host.db");
    private AuthenticatedHostInstance Caller => new(receiver.LibraryId, origin);
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AppliesAllFileKindsAndPreservesOriginalsAndNeighboringShares()
    {
        Directory.CreateDirectory(Work);
        var old = await WriteAsync(ExportRoot, "Old/Move.flac", "move audio");
        var replaced = await WriteAsync(ExportRoot, "Lists/Keep.m3u8", "old playlist");
        var retired = await WriteAsync(ExportRoot, "Lists/Retired.m3u8", "retired playlist");
        var source = await WriteAsync(Path.Combine(root, "source"), "Added.flac", "new audio");
        var other = await WriteAsync(Path.Combine(root, "other"), "Lists/Retired.m3u8", "outside share");
        var content = new FileStagedPlaylistContent(Path.Combine(root, "staged"));
        var addition = await StageAsync(content, ExportContentKind.Audio, "Library/Added.flac", "new audio");
        var replacement = await StageAsync(content, ExportContentKind.Playlist, "Lists/Keep.m3u8", "new playlist");
        var moved = await StageAsync(content, ExportContentKind.Audio, "Old/Move.flac", "move audio");
        var oldPlaylist = await StageAsync(content, ExportContentKind.Playlist, "Lists/Keep.m3u8", "old playlist");
        var oldRetired = await StageAsync(content, ExportContentKind.Playlist, "Lists/Retired.m3u8", "retired playlist");
        var id = Guid.NewGuid();
        var work = new PendingDeviceExportPublication(id, new(origin, Guid.NewGuid()), Guid.NewGuid(), 1, receiver, 0, 0,
            [new(DeviceExportOperationKind.Add, null, addition), new(DeviceExportOperationKind.Replace, oldPlaylist, replacement),
                new(DeviceExportOperationKind.Move, moved, moved with { Path = "Library/Moved.flac" }), new(DeviceExportOperationKind.Remove, oldRetired, null)]);
        var writer = new FileDeviceExportStepWriter(ExportRoot, Work, receiver, content);
        for (var ordinal = 0; ordinal < work.Operations.Length; ordinal++)
            Assert.True(Value(await writer.ApplyAsync(work, ordinal, Token)));

        var reopenedWriter = new FileDeviceExportStepWriter(ExportRoot, Work, receiver, content);
        var replayed = true;
        for (var ordinal = 0; ordinal < work.Operations.Length; ordinal++)
            replayed &= Value(await reopenedWriter.ApplyAsync(work, ordinal, Token));
        var foreign = work with { PublicationId = Guid.NewGuid(), Operations = [new(DeviceExportOperationKind.Add, null, addition)] };
        var rejected = Assert.IsType<HostOutcome<bool>.Rejected>(await reopenedWriter.ApplyAsync(foreign, 0, Token));
        var backups = Directory.GetFiles(Path.Combine(Work, id.ToString("N")), "*.original");
        var retained = await Task.WhenAll(backups.Select(path => File.ReadAllTextAsync(path, Token)));
        StageAssertions.VerifyExportFileEffects(new(
            await File.ReadAllTextAsync(Path.Combine(ExportRoot, "Library", "Added.flac"), Token), await File.ReadAllTextAsync(replaced, Token),
            await File.ReadAllTextAsync(Path.Combine(ExportRoot, "Library", "Moved.flac"), Token), !File.Exists(old), !File.Exists(retired),
            await File.ReadAllTextAsync(source, Token), await File.ReadAllTextAsync(other, Token), rejected.Code, replayed,
            [.. retained.Order(StringComparer.Ordinal)]));
        Assert.Empty(Directory.EnumerateFiles(ExportRoot, ".syncthing.*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ChangedStagedBytesAndChangedOwnedFilesDoNotModifyTheShare()
    {
        Directory.CreateDirectory(Work);
        var stagedPath = Path.Combine(root, "staged");
        var content = new FileStagedPlaylistContent(stagedPath);
        var older = await StageAsync(content, ExportContentKind.Playlist, "Lists/Ready.m3u8", "old playlist");
        var latest = await StageAsync(content, ExportContentKind.Playlist, older.Path, "new playlist");
        var target = await WriteAsync(ExportRoot, older.Path, "old playlist");
        await File.WriteAllTextAsync(Path.Combine(stagedPath, latest.ContentIdentity), "corrupted staged bytes", Token);
        var work = new PendingDeviceExportPublication(Guid.NewGuid(), new(origin, Guid.NewGuid()), Guid.NewGuid(), 1, receiver, 0, 0,
            [new(DeviceExportOperationKind.Replace, older, latest)]);
        var writer = new FileDeviceExportStepWriter(ExportRoot, Work, receiver, content);
        var wrongSource = Assert.IsType<HostOutcome<bool>.Rejected>(await writer.ApplyAsync(work, 0, Token));
        Assert.Equal("export-staged-bytes-changed", wrongSource.Code);
        Assert.Equal("old playlist", await File.ReadAllTextAsync(target, Token));
        Assert.Empty(Directory.EnumerateFiles(Work, "*.started", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(ExportRoot, ".syncthing.*.tmp", SearchOption.AllDirectories));

        await File.WriteAllTextAsync(target, "changed out of band", Token);
        var remove = work with { PublicationId = Guid.NewGuid(), Operations = [new(DeviceExportOperationKind.Remove, older, null)] };
        var wrongOwner = Assert.IsType<HostOutcome<bool>.Rejected>(await writer.ApplyAsync(remove, 0, Token));
        Assert.Equal("export-file-changed", wrongOwner.Code);
        Assert.Equal("changed out of band", await File.ReadAllTextAsync(target, Token));
    }

    [Fact]
    public async Task ResumesAfterAFileWasAppliedBeforeItsJournalStepWasRecorded()
    {
        Directory.CreateDirectory(ExportRoot);
        Directory.CreateDirectory(Work);
        var content = new FileStagedPlaylistContent(Path.Combine(root, "staged"));
        var added = await StageAsync(content, ExportContentKind.Audio, "Library/A.flac", "new audio");
        using (LiteDbHostPlaylistStore.Open(DatabasePath, true)) { }
        using (var seed = new LiteDatabase(DatabasePath))
        {
            seed.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = origin, ["LibraryId"] = receiver.LibraryId, ["Role"] = "Desktop", ["Status"] = "Active" });
            TestAndroidRegistration.Seed(seed, receiver);
        }

        DeviceExportPublicationIntent intent;
        PendingDeviceExportPublication recorded;
        string playlistPath;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var saved = Value(await new HostPlaylistWriter(store).SaveAsync(receiver.LibraryId, Caller,
                new(origin, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Ready", [new(Guid.NewGuid(), added.Source.EntityId)]), Token));
            var stager = new HostDevicePlaylistStager(store, new SingleTrackReceiver(receiver), new([new M3u8PlaylistFormatter()]), content);
            var plan = Value(await stager.StageAsync(receiver.LibraryId, Caller,
                new(origin, Guid.NewGuid(), saved.PlaylistId, receiver.DeviceId, "musicolet", 2, null, null), Token)).Plan;
            var playlist = Assert.Single(plan.Entries.OfType<ExportEntry.Playlist>());
            playlistPath = playlist.Destination;
            var output = await StageAsync(content, ExportContentKind.Playlist, playlistPath, "#EXTM3U\n../Library/A.flac\n");
            var initialManifest = Value(await store.ReadPublishedManifestAsync(receiver.LibraryId, Caller, receiver.DeviceId, Token));
            var empty = new DeviceExportShareSnapshot(receiver.LibraryId, receiver.DeviceId, receiver.ShareId, []);
            var prepared = Value(new DeviceExportPublicationPlanner().Prepare(plan, receiver, initialManifest, empty, DeviceExportPublicationMode.AddToShare,
                [new(added.Source, added.Path, true, true, added.ContentIdentity),
                    new(new(ExportContentKind.Playlist, playlist.PlaylistId), playlistPath, true, true, output.ContentIdentity)]));
            intent = new(Guid.NewGuid(), plan.PlanId!.Value, plan.PlanRevision!.Value);
            recorded = Assert.IsType<DeviceExportPublicationReservation.Pending>(Value(await store.ReservePublicationAsync(receiver.LibraryId,
                Caller, intent, prepared, Token))).Work;
            Assert.True(Value(await new FileDeviceExportStepWriter(ExportRoot, Work, receiver, content).ApplyAsync(recorded, 0, Token)));
            Assert.Equal(0, Value(await store.ReadPublishedManifestAsync(receiver.LibraryId, Caller, receiver.DeviceId, Token)).Revision);
        }

        Assert.True(File.Exists(Path.Combine(ExportRoot, "Library", "A.flac")));
        Assert.False(File.Exists(Path.Combine(ExportRoot, playlistPath)));
        using var reopened = LiteDbHostPlaylistStore.Open(DatabasePath, false);
        var current = Assert.IsType<DeviceExportPublicationReservation.Pending>(Value(await reopened.FindPublicationAsync(receiver.LibraryId,
            Caller, intent, Token))).Work;
        Assert.Equal(0, current.NextStep);
        var publisher = new HostDeviceExportPublisher(reopened, reopened, new FileDeviceExportStepWriter(ExportRoot, Work, receiver, content));
        var parallel = await Task.WhenAll(publisher.ResumeAsync(receiver.LibraryId, Caller, intent, Token),
            publisher.ResumeAsync(receiver.LibraryId, Caller, intent, Token));
        var applied = Value(parallel[0]);
        Assert.Equal(applied, Value(parallel[1]));
        Assert.Equal(recorded.PublicationId, applied.PublicationId);
        Assert.Equal(1, applied.ManifestRevision);
        Assert.Equal("#EXTM3U\n../Library/A.flac\n", await File.ReadAllTextAsync(Path.Combine(ExportRoot, playlistPath), Token));
        Assert.Equal(applied, Value(await publisher.ResumeAsync(receiver.LibraryId, Caller, intent, Token)));
        Assert.Equal(2, Value(await reopened.ReadPublishedManifestAsync(receiver.LibraryId, Caller, receiver.DeviceId, Token)).Files.Length);
    }

    private async Task<string> WriteAsync(string directory, string relative, string bytes)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, bytes, Token);
        return path;
    }

    private async Task<OwnedDeviceExportFile> StageAsync(FileStagedPlaylistContent store, ExportContentKind kind, string path, string content) =>
        new(new(kind, Guid.NewGuid()), path, await store.StoreAsync([.. Encoding.UTF8.GetBytes(content)], Token));

    private static T Value<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;

    private sealed class SingleTrackReceiver(RegisteredAndroidExport target) : IHostDevicePlaylistResolver
    {
        public Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token) =>
            Task.FromResult<HostOutcome<ResolvedDevicePlaylist>>(new HostOutcome<ResolvedDevicePlaylist>.Success(new(target,
                new(playlist.PlaylistId, target.DeviceId, "musicolet", 2, "m3u8", "Lists/Ready.m3u8", target.CaseSensitivePaths,
                    [new(Assert.Single(playlist.Entries).TrackId, "Library/A.flac", false, true)]))));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
