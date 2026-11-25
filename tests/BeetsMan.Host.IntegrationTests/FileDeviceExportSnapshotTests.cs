using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Observes real share files and ownership drift for the Android publication preview.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-002 to FR-FILESYNC-001#ac-safety without publishing to an existing device.</remarks>
public sealed class FileDeviceExportSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-export-observation-{Guid.NewGuid():N}");
    private readonly RegisteredAndroidExport destination = new(Guid.NewGuid(), Guid.NewGuid(), 3, "android-export", "fixture-receiver",
        new("https://receiver.test/"), false);
    private string ExportRoot => Path.Combine(root, "android-share");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PhysicalSnapshotReadsOnlySelectedPathsAndMakesChangedOwnershipBlockThePreview()
    {
        Directory.CreateDirectory(Path.Combine(ExportRoot, "Library"));
        Directory.CreateDirectory(Path.Combine(root, "other-share"));
        var ownedPath = Path.Combine(ExportRoot, "Library", "Existing.flac");
        var other = Path.Combine(root, "other-share", "Protected.flac");
        await File.WriteAllTextAsync(ownedPath, "abc", Token);
        await File.WriteAllTextAsync(Path.Combine(ExportRoot, "Collision"), "blocking file", Token);
        await File.WriteAllTextAsync(Path.Combine(ExportRoot, "Other.flac"), "outside the preview", Token);
        await File.WriteAllTextAsync(other, "neighboring share", Token);

        var reader = new FileDeviceExportSnapshotReader(ExportRoot, destination);
        string[] paths = ["library/existing.flac", "Library/Absent.flac", "Collision/Track.flac"];
        var snapshot = Value(await reader.ReadAsync(destination, paths, Token));
        var file = Assert.Single(snapshot.Paths, entry => entry.Path.EndsWith(".flac", StringComparison.Ordinal));
        var collision = Assert.Single(snapshot.Paths, entry => entry.Path == "Collision");
        var wrongScope = Assert.IsType<HostOutcome<DeviceExportShareSnapshot>.Rejected>(await reader.ReadAsync(
            destination with { ShareId = "other-share" }, paths, Token));
        StageAssertions.VerifyExportShareRead(new(file.Path, file.ContentIdentity!, collision.Path, collision.Kind.ToString(),
            snapshot.Paths.All(path => path.Path != "Library/Absent.flac"), snapshot.Paths.All(path => path.Path != "Other.flac"), wrongScope.Code));

        var owned = new OwnedDeviceExportFile(new(ExportContentKind.Audio, Guid.NewGuid()), file.Path, file.ContentIdentity!);
        var manifest = new PublishedDeviceExportManifest(destination.LibraryId, destination.DeviceId, destination.ShareId, 4, [owned]);
        var plan = new DeviceExportPlan(Guid.NewGuid(), 1, destination.DeviceId, "musicolet", 2, []);
        var planner = new DeviceExportPublicationPlanner();
        var preview = Value(planner.Prepare(plan, destination, manifest, snapshot, DeviceExportPublicationMode.ReconcileLayout, []));
        Assert.Equal(file.Path, Assert.Single(preview.RemovalProposal!.Paths));

        await File.WriteAllTextAsync(ownedPath, "changed outside the manifest", Token);
        var updated = Value(await reader.ReadAsync(destination, paths, Token));
        var blocked = Value(planner.Prepare(plan, destination, manifest, updated, DeviceExportPublicationMode.ReconcileLayout, []));
        Assert.Equal(new DeviceExportPreviewIssue("export-owned-content-changed", file.Path), Assert.Single(blocked.Issues));
        Assert.Null(blocked.RemovalProposal);
        Assert.Equal("changed outside the manifest", await File.ReadAllTextAsync(ownedPath, Token));
        Assert.Equal("neighboring share", await File.ReadAllTextAsync(other, Token));
    }

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task LinkedSubdirectoryIsReportedWithoutReadingItsTarget()
    {
        Directory.CreateDirectory(ExportRoot);
        var foreign = Path.Combine(root, "other-share");
        Directory.CreateDirectory(foreign);
        await File.WriteAllTextAsync(Path.Combine(foreign, "Other.flac"), "neighboring file", Token);
        Directory.CreateSymbolicLink(Path.Combine(ExportRoot, "Library"), foreign);

        var reader = new FileDeviceExportSnapshotReader(ExportRoot, destination);
        var snapshot = Value(await reader.ReadAsync(destination, ["Library/Other.flac"], Token));
        Assert.Equal(new ObservedDeviceExportPath("Library", DeviceExportPathKind.Link), Assert.Single(snapshot.Paths));
    }

    private static T Value<T>(HostOutcome<T> result) => Assert.IsType<HostOutcome<T>.Success>(result).Value;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
