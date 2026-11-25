using BeetsMan.Desktop.Application.MediaSync;
using Xunit;
using Xunit.Sdk;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves saved-playlist export assertions before the staging implementation is bound.</summary>
/// <remarks>2026-11-24: Exercise the independent mock gate for TEST-FILESYNC-001 and FR-FILESYNC-001#ac-playlist-drop.</remarks>
public sealed class PlaylistExportMockTests
{
    [Fact]
    public void StagedOutputAssertionsRejectExtraAudioAndChangedPlaylistOrder()
    {
        var present = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var playlistId = Guid.NewGuid();
        var playlist = new ExportEntry.Playlist(Guid.NewGuid(), "playlists/Mix.m3u8", playlistId, "m3u8", [
            new(present, "music/Present.flac", ExportAudioStatus.Present),
            new(shared, "music/Shared.flac", ExportAudioStatus.Staged),
            new(missing, "music/Missing.flac", ExportAudioStatus.Staged),
            new(present, "music/Present.flac", ExportAudioStatus.Present)
        ]);
        ExportEntry[] expected = [playlist, new ExportEntry.Audio(Guid.NewGuid(), "music/Missing.flac", missing, ExportAudioStatus.Staged)];
        var mock = new PlaylistExportFixture(expected);
        PlaylistExportAssertions.Verify(mock.Stage(), "m3u8", [present, shared, missing, present], [missing]);
        Assert.ThrowsAny<XunitException>(() => PlaylistExportAssertions.Verify(mock.Stage(), "wpl", [present, shared, missing, present], [missing]));
        Assert.ThrowsAny<XunitException>(() => PlaylistExportAssertions.Verify(mock.Stage(), "m3u8", [missing, shared, present, present], [missing]));
        var incorrect = new PlaylistExportFixture([.. expected, new ExportEntry.Audio(Guid.NewGuid(), "music/Present.flac", present, ExportAudioStatus.Staged)]);
        Assert.ThrowsAny<XunitException>(() => PlaylistExportAssertions.Verify(incorrect.Stage(), "m3u8", [present, shared, missing, present], [missing]));
    }

    private sealed class PlaylistExportFixture(ExportEntry[] entries)
    {
        public ExportEntry[] Stage() => [.. entries];
    }
}

internal static class PlaylistExportAssertions
{
    public static void Verify(IEnumerable<ExportEntry> staged, string expectedFormat, Guid[] orderedTracks, Guid[] newAudio)
    {
        var playlist = Assert.Single(staged.OfType<ExportEntry.Playlist>());
        VerifyEntries(playlist.Format, playlist.Tracks.Select(track => track.TrackId), staged.OfType<ExportEntry.Audio>().Select(entry => entry.TrackId),
            expectedFormat, orderedTracks, newAudio);
    }

    /// <summary>Checks the authoritative host export using the same fixture assertions.</summary>
    public static void Verify(IEnumerable<Wire.ExportEntry> staged, string expectedFormat, Guid[] orderedTracks, Guid[] newAudio)
    {
        var playlist = Assert.Single(staged.OfType<Wire.ExportEntry.Playlist>());
        VerifyEntries(playlist.Format, playlist.Tracks.Select(track => track.TrackId), staged.OfType<Wire.ExportEntry.Audio>().Select(entry => entry.TrackId),
            expectedFormat, orderedTracks, newAudio);
    }

    private static void VerifyEntries(string format, IEnumerable<Guid> references, IEnumerable<Guid> audio,
        string expectedFormat, Guid[] orderedTracks, Guid[] newAudio)
    {
        Assert.Equal(expectedFormat, format);
        Assert.Equal(orderedTracks, references);
        Assert.Equal(newAudio, audio);
    }
}
