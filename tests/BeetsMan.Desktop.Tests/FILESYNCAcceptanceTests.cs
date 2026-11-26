using System.Collections.Immutable;
using System.Text;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Host.Application;
using Xunit;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Tests;

/// <summary>Exercises the staged-playlist rules through the real planner and typed host boundary.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-001 to FR-FILESYNC-001#ac-playlist-drop and ac-safety using independent device inventory.</remarks>
public sealed partial class FILESYNCAcceptanceTests
{
    [Fact]
    public void PlaylistDrop_MatchesContract()
    {
        var device = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var present = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var existing = new Wire.DeviceExportPlan(Guid.NewGuid(), 8, device, "musicolet", 2,
            [new Wire.ExportEntry.Audio(Guid.NewGuid(), "music/Shared.flac", shared, Wire.ExportAudioStatus.Staged)]);
        var source = new ResolvedPlaylistExport(playlist, device, "musicolet", 2, "m3u8", "playlists/Mix.m3u8", false,
            [new(present, "music/Present.flac", true, false), new(shared, "music/Shared.flac", false, false),
                new(missing, "music/Míssing.flac", false, true), new(present, "music/Present.flac", true, false)]);
        var command = new Wire.StageDevicePlaylistRequest(Guid.NewGuid(), Guid.NewGuid(), playlist, device, "musicolet", 2, existing.PlanId, existing.PlanRevision);
        var planner = new PlaylistExportPlanner([new M3u8PlaylistFormatter()]);

        var staged = RequireSuccess(planner.Stage(command, source, existing));
        var newFiles = staged.Plan.Entries.Where(entry => existing.Entries.All(prior => prior.EntryId != entry.EntryId));
        PlaylistExportAssertions.Verify(newFiles, "m3u8", [present, shared, missing, present], [missing]);
        Assert.Equal(existing.PlanId, staged.Plan.PlanId);
        Assert.Equal(9, staged.Plan.PlanRevision);
        Assert.Equal("#EXTM3U\n../music/Present.flac\n../music/Shared.flac\n../music/Míssing.flac\n../music/Present.flac\n",
            Encoding.UTF8.GetString(staged.PlaylistContent.AsSpan()));

        var next = RequireSuccess(planner.Stage(command with { ExpectedPlanRevision = 9 }, source, staged.Plan));
        Assert.Equal(staged.PlaylistEntryId, next.PlaylistEntryId);
        Assert.Equal(staged.Plan.Entries.Select(entry => entry.EntryId), next.Plan.Entries.Select(entry => entry.EntryId));
        Assert.Equal(staged.PlaylistContent.ToArray(), next.PlaylistContent.ToArray());
        Assert.True(Assert.IsType<HostOutcome<PlaylistStageMutation>.Rejected>(planner.Stage(command, source, staged.Plan)).Conflict);
    }

    [Fact]
    public void FullyPresentAudioAndSecondProfileStageOnlyTheirPlaylistFile()
    {
        var formatter = new FixtureAlternateFormatter();
        var planner = new PlaylistExportPlanner([new M3u8PlaylistFormatter(), formatter]);
        var device = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var source = new ResolvedPlaylistExport(playlist, device, "other-app", 3, "fixture-format", "Other/Mix.test", true,
            [new(first, "Other/A.flac", true, false), new(second, "Other/B.flac", true, false)]);
        var request = new Wire.StageDevicePlaylistRequest(Guid.NewGuid(), Guid.NewGuid(), playlist, device, "other-app", 3, null, null);
        var staged = RequireSuccess(planner.Stage(request, source, null));

        PlaylistExportAssertions.Verify(staged.Plan.Entries, "fixture-format", [first, second], []);
        Assert.Equal(["A.flac", "B.flac"], formatter.ObservedTracks);
        Assert.Equal("fixture-format", Encoding.UTF8.GetString(staged.PlaylistContent.AsSpan()));
        Assert.Equal("unsupported-playlist-format", Assert.IsType<HostOutcome<PlaylistStageMutation>.Rejected>(
            planner.Stage(request, source with { Format = "unsupported" }, null)).Code);
    }

    [Theory]
    [InlineData("../../private.flac", "invalid-track-destination")]
    [InlineData("Music/CON.flac", "invalid-track-destination")]
    [InlineData("Music/.syncthing.export.tmp", "invalid-track-destination")]
    [InlineData("Music/.stignore", "invalid-track-destination")]
    [InlineData("Playlist.m3u8/track.flac", "invalid-track-destination")]
    [InlineData("mUSic/track.flac", "audio-destination-collision")]
    public void InvalidOrCollidingDestinationsHaveNoStagedMutation(string path, string issue)
    {
        var device = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var source = new ResolvedPlaylistExport(playlist, device, "player", 1, "m3u8", "Playlist.m3u8", false,
            [new(Guid.NewGuid(), "Music/Track.flac", true, true), new(Guid.NewGuid(), path, false, true)]);
        var request = new Wire.StageDevicePlaylistRequest(Guid.NewGuid(), Guid.NewGuid(), playlist, device, "player", 1, null, null);
        var planner = new PlaylistExportPlanner([new M3u8PlaylistFormatter()]);
        Assert.Equal(issue, Assert.IsType<HostOutcome<PlaylistStageMutation>.Rejected>(planner.Stage(request, source, null)).Code);
    }

    [Fact]
    public async Task TypedDesktopHandlerForwardsTheExactOperationToTheHost()
    {
        var command = new StagePlaylistForDevice(Guid.NewGuid(), Guid.NewGuid(), "player", 1, null, null);
        var plan = new DeviceExportPlan(Guid.NewGuid(), 1, command.DeviceId, command.ProfileId, 1, []);
        var host = new RecordingHost(plan);
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand(new StagePlaylistForDeviceHandler(host)).Build(typeof(StagePlaylistForDevice));
        var context = new CommandContext(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
        var result = await dispatcher.SendAsync(command, context, TestContext.Current.CancellationToken);
        Assert.Same(plan, Assert.IsType<Outcome<DeviceExportPlan>.Success>(result).Value);
        Assert.Same(command, host.Request);
        Assert.Equal(context, host.Context);
    }

    private static PlaylistStageMutation RequireSuccess(HostOutcome<PlaylistStageMutation> result) =>
        Assert.IsType<HostOutcome<PlaylistStageMutation>.Success>(result).Value;

    private sealed class FixtureAlternateFormatter : IExportPlaylistFormatter
    {
        public string Format => "fixture-format";
        public ImmutableArray<string> ObservedTracks { get; private set; }
        public ImmutableArray<byte> FormatTracks(ImmutableArray<string> orderedRelativeTracks)
        {
            ObservedTracks = orderedRelativeTracks;
            return [.. Encoding.UTF8.GetBytes(Format)];
        }
    }

    private sealed class RecordingHost(DeviceExportPlan plan) : IPlaylistExportHost
    {
        public StagePlaylistForDevice? Request { get; private set; }
        public CommandContext Context { get; private set; }

        public Task<Outcome<DeviceExportPlan>> StagePlaylistAsync(StagePlaylistForDevice request, CommandContext context, CancellationToken cancellationToken)
        {
            Request = request;
            Context = context;
            return Task.FromResult<Outcome<DeviceExportPlan>>(new Outcome<DeviceExportPlan>.Success(plan));
        }
    }
}
