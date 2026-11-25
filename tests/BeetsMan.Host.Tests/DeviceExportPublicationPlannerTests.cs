using System.Collections.Immutable;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks the publication preview against independent owned, foreign, current and desired files.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-001 to FR-FILESYNC-001#ac-safety and ac-playlist-drop.</remarks>
public sealed class DeviceExportPublicationPlannerTests
{
    private static readonly string NewDigest = new('A', 64);
    private static readonly string MoveDigest = new('B', 64);
    private static readonly string RetiredDigest = new('C', 64);
    private static readonly string ForeignDigest = new('D', 64);
    private readonly DeviceExportPublicationPlanner planner = new();

    [Fact]
    public void PreviewReportsReviewedMovesAndOnlyTheOwnedRemovalThatNeedsApproval()
    {
        var fixture = Fixture();
        var preview = Prepare(fixture);
        var proposal = Assert.IsType<DeviceExportRemovalProposal>(preview.RemovalProposal);
        Assert.Equal(proposal.ProposalId, Prepare(fixture with { Files = [.. fixture.Files.Reverse()] }).RemovalProposal!.ProposalId);
        var pending = Value(DeviceExportPublicationPlanner.CheckApproval(preview, null));
        Assert.Same(proposal, pending);
        var approval = new DeviceExportRemovalApproval(proposal.ProposalId, proposal.PlanId, proposal.PlanRevision);
        var accepted = DeviceExportPublicationPlanner.CheckApproval(preview, approval);
        var stale = Prepare(fixture with { Manifest = fixture.Manifest with { Revision = fixture.Manifest.Revision + 1 } });
        Assert.NotEqual(proposal.ProposalId, stale.RemovalProposal!.ProposalId);
        var staleResult = DeviceExportPublicationPlanner.CheckApproval(stale, approval);
        var changedOwnership = fixture.Share with { Paths = [new("CURRENT/New.flac", DeviceExportPathKind.File, ForeignDigest),
            .. fixture.Share.Paths.Where(item => item.Path != "Old/Retired.m3u8"), new("Old/Retired.m3u8", DeviceExportPathKind.File, ForeignDigest)] };
        var blocked = Prepare(fixture with { Share = changedOwnership, Files = [.. fixture.Files,
            new(new(ExportContentKind.Audio, Guid.NewGuid()), "Current/Unreadable.flac", true, false, null)] });
        Assert.Null(blocked.RemovalProposal);
        Assert.Equal("export-preview-blocked", Assert.IsType<HostOutcome<DeviceExportRemovalProposal?>.Rejected>(
            DeviceExportPublicationPlanner.CheckApproval(blocked, approval)).Code);

        StageAssertions.VerifyExportPreview(new(
            [.. preview.Operations.Where(file => file.Kind == DeviceExportOperationKind.Add).Select(file => file.After!.Path)],
            [.. preview.Operations.Where(file => file.Kind == DeviceExportOperationKind.Move).Select(file => $"{file.Before!.Path} -> {file.After!.Path}")],
            [.. preview.Operations.Where(file => file.Kind == DeviceExportOperationKind.Remove).Select(file => file.Before!.Path)],
            [.. proposal.Paths], [.. blocked.Issues.Select(issue => issue.Code).Order(StringComparer.Ordinal)],
            accepted is HostOutcome<DeviceExportRemovalProposal?>.Success { Value: null },
            staleResult is HostOutcome<DeviceExportRemovalProposal?>.Rejected { Code: "export-removal-approval-stale", Conflict: true },
            preview.NextOwnedFiles.All(file => file.Path != "Other/Unowned.flac")));
    }

    [Fact]
    public void AddingAPlaylistPreservesEarlierOwnedFilesOutsideTheStagedList()
    {
        var fixture = Fixture();
        var preview = Value(planner.Prepare(fixture.Plan, fixture.Destination, fixture.Manifest, fixture.Share,
            DeviceExportPublicationMode.AddToShare, [fixture.Files[0]]));
        Assert.Equal(DeviceExportOperationKind.Add, Assert.Single(preview.Operations).Kind);
        Assert.Null(preview.RemovalProposal);
        Assert.All(fixture.Manifest.Files, file => Assert.Contains(file, preview.NextOwnedFiles));
        Assert.DoesNotContain(preview.NextOwnedFiles, file => file.Path == "Other/Unowned.flac");
    }

    [Theory]
    [InlineData(DeviceExportPathKind.File)]
    [InlineData(DeviceExportPathKind.Link)]
    public void AFileOrLinkAtAnAncestorBlocksPublication(DeviceExportPathKind kind)
    {
        var fixture = Fixture();
        var share = fixture.Share with { Paths = [.. fixture.Share.Paths, new("Current", kind, NewDigest)] };
        var preview = Prepare(fixture with { Share = share });
        Assert.Contains(new DeviceExportPreviewIssue("export-destination-collision", "Current/New.flac"), preview.Issues);
        Assert.DoesNotContain(preview.Operations, item => item.After?.Path == "Current/New.flac");
    }

    [Fact]
    public void SnapshotsFromAnotherShareOrDeviceCannotAuthorizeChanges()
    {
        var fixture = Fixture();
        var result = planner.Prepare(fixture.Plan, fixture.Destination, fixture.Manifest with { ShareId = "pc-mirror" }, fixture.Share,
            DeviceExportPublicationMode.ReconcileLayout, fixture.Files);
        Assert.Equal("invalid-export-publication-scope", Assert.IsType<HostOutcome<PreparedDeviceExportPublication>.Rejected>(result).Code);
        var differentDevice = planner.Prepare(fixture.Plan, fixture.Destination, fixture.Manifest, fixture.Share with { DeviceId = Guid.NewGuid() },
            DeviceExportPublicationMode.ReconcileLayout, fixture.Files);
        Assert.Equal("invalid-export-publication-scope", Assert.IsType<HostOutcome<PreparedDeviceExportPublication>.Rejected>(differentDevice).Code);
    }

    private PreparedDeviceExportPublication Prepare(ExportFixture fixture) => Value(planner.Prepare(fixture.Plan, fixture.Destination,
        fixture.Manifest, fixture.Share, DeviceExportPublicationMode.ReconcileLayout, fixture.Files));

    private static ExportFixture Fixture()
    {
        var library = Guid.NewGuid();
        var device = Guid.NewGuid();
        var target = new RegisteredAndroidExport(library, device, 5, "registration-share", "receiver", new("https://receiver.test/"), false);
        var plan = new DeviceExportPlan(Guid.NewGuid(), 12, device, "poweramp", 3, []);
        var movedTrack = new ExportContentSource(ExportContentKind.Audio, Guid.NewGuid());
        var retiredPlaylist = new ExportContentSource(ExportContentKind.Playlist, Guid.NewGuid());
        var owned = new PublishedDeviceExportManifest(library, device, target.ShareId, 7,
            [new(movedTrack, "Old/Move.flac", MoveDigest), new(retiredPlaylist, "Old/Retired.m3u8", RetiredDigest)]);
        var share = new DeviceExportShareSnapshot(library, device, target.ShareId, [new("Old/Move.flac", DeviceExportPathKind.File, MoveDigest),
            new("Old/Retired.m3u8", DeviceExportPathKind.File, RetiredDigest), new("Other/Unowned.flac", DeviceExportPathKind.File, ForeignDigest)]);
        return new(plan, target, owned, share, [new(new(ExportContentKind.Audio, Guid.NewGuid()), "Current/New.flac", true, true, NewDigest),
            new(movedTrack, "Current/Move.flac", true, true, MoveDigest),
            new(new(ExportContentKind.Audio, Guid.NewGuid()), "Other/Unowned.flac", true, true, ForeignDigest)]);
    }

    private static T Value<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;

    private sealed record ExportFixture(DeviceExportPlan Plan, RegisteredAndroidExport Destination, PublishedDeviceExportManifest Manifest,
        DeviceExportShareSnapshot Share, ImmutableArray<DeviceExportFileCandidate> Files);
}
