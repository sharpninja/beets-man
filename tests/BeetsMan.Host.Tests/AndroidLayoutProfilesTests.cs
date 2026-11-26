using System.Text;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies bundled and operator-provided layouts through the actual host parser and planner.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-001 to FR-FILESYNC-001#ac-layouts, ac-safety and ac-playlist-drop.</remarks>
public sealed partial class FILESYNCAcceptanceTests
{
    private const string AlternateLayout = """
        id: poweramp-pls
        player: Poweramp
        templateVersion: 3
        suggestedRoot: /storage/emulated/0/Music/Beets
        audioPathTemplate: 'Library/{fileName}'
        playlistFormat: pls
        playlistPathTemplate: 'Lists/{playlistName}.{playlistFormat}'
        """;

    /// <summary>Validates the default album layout and an independent alternate playlist format.</summary>
    /// <remarks>2026-11-26: Run the named TEST-FILESYNC-001 layout case for FR-FILESYNC-001#ac-layouts.</remarks>
    [Fact]
    public void Layouts_MatchesContract()
    {
        IExportPlaylistFormatter[] formatters = [new M3u8PlaylistFormatter(), new PlsPlaylistFormatter()];
        var profiles = new AndroidLayoutProfiles(formatters);
        var builtIn = profiles.LoadBuiltIn();
        Assert.Equal(2, builtIn.Length);
        var defaultProfile = Assert.Single(builtIn, item => item.Id == AndroidLayoutProfiles.DefaultId);
        var poweramp = Assert.Single(builtIn, item => item.Id == "poweramp");
        Assert.Equal(3, poweramp.Version);
        Assert.Equal("playlists/Night Drive.m3u8", poweramp.ResolvePlaylistPath("Night Drive"));

        var alternate = Value(profiles.Parse(AlternateLayout));
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var device = Guid.NewGuid();
        var request = new StageDevicePlaylistRequest(Guid.NewGuid(), Guid.NewGuid(), playlist, device, alternate.Id, alternate.Version, null, null);
        var destination = alternate.ResolvePlaylistPath("Night Drive")!;
        var resolved = new ResolvedPlaylistExport(playlist, device, alternate.Id, alternate.Version, alternate.PlaylistFormat!, destination, false,
            [new(b, alternate.ResolveAudioPath(null, null, null, "B.flac")!, true, false),
                new(a, alternate.ResolveAudioPath(null, null, null, "A.flac")!, false, true),
                new(b, alternate.ResolveAudioPath(null, null, null, "B.flac")!, true, false)]);
        var staged = Value(new PlaylistExportPlanner(formatters).Stage(request, resolved, null));
        Assert.Equal(a, Assert.Single(staged.Plan.Entries.OfType<ExportEntry.Audio>()).TrackId);
        var file = Assert.Single(staged.Plan.Entries.OfType<ExportEntry.Playlist>());
        Assert.Equal("pls", file.Format);
        Assert.Equal(new[] { b, a, b }, file.Tracks.Select(track => track.TrackId));
        StageAssertions.VerifyAndroidLayout(new(defaultProfile.Id, defaultProfile.Version, defaultProfile.SuggestedRoot,
            defaultProfile.ResolveAudioPath("Lena Vale", "Other Performer", "Northern Lines", "01 Harbor Lights.flac")!,
            defaultProfile.ResolvePlaylistPath("Night Drive")!, file.Destination, Encoding.UTF8.GetString(staged.PlaylistContent.AsSpan()),
            Rejection(profiles.Parse(AlternateLayout.Replace("Library/{fileName}", "../{fileName}", StringComparison.Ordinal))),
            Rejection(profiles.Parse(AlternateLayout + "\nplayer: Other")),
            Rejection(profiles.Parse(AlternateLayout.Replace("playlistFormat: pls", "playlistFormat: unknown", StringComparison.Ordinal)))));
    }

    [Fact]
    public void TemplateMetadataCannotInsertNewPathSegments()
    {
        var profile = new AndroidLayoutProfiles([new M3u8PlaylistFormatter()]).LoadBuiltIn()[0];
        Assert.Equal("AC_DC/_CON/Track.flac", profile.ResolveAudioPath("AC/DC", null, "CON", "Track.flac"));
        Assert.Equal("playlists/Old_New.m3u8", profile.ResolvePlaylistPath("Old/New"));
        Assert.Null(profile.ResolveAudioPath(null, null, null, "../Track.flac"));
    }

    [Theory]
    [InlineData("Library/{unknown}/{fileName}")]
    [InlineData("/Library/{fileName}")]
    [InlineData("Library/CON/{fileName}")]
    public void RejectsUnrecognizedTokensAndUnsafeStaticPaths(string template)
    {
        var parser = new AndroidLayoutProfiles([new PlsPlaylistFormatter()]);
        Assert.Equal("invalid-device-layout-path", Rejection(parser.Parse(AlternateLayout.Replace("Library/{fileName}", template, StringComparison.Ordinal))));
    }

    [Fact]
    public void SupportsAudioOnlyLayoutWithoutImplicitPlaylistFormat()
    {
        const string audioOnly = """
            id: audio-only
            player: Audio Player
            templateVersion: 1
            suggestedRoot: /sdcard/Music
            audioPathTemplate: '{fileName}'
            """;
        var profile = Value(new AndroidLayoutProfiles([]).Parse(audioOnly));
        Assert.Equal("Track.flac", profile.ResolveAudioPath(null, null, null, "Track.flac"));
        Assert.Null(profile.PlaylistFormat);
        Assert.Null(profile.ResolvePlaylistPath("Night Drive"));
    }

    private static string Rejection(HostOutcome<AndroidLayoutProfile> result) => Assert.IsType<HostOutcome<AndroidLayoutProfile>.Rejected>(result).Code;
    private static T Value<T>(HostOutcome<T> result) => Assert.IsType<HostOutcome<T>.Success>(result).Value;
}
