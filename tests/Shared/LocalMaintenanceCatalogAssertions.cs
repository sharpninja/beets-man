using BeetsMan.Desktop.Application.Library;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Defines the working-catalog behavior independently of LiteDB and the desktop projection.</summary>
/// <remarks>2026-11-24: Cover the local maintenance effects of TEST-LIBRARY-003/004 for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
internal static class LocalMaintenanceCatalogAssertions
{
    internal const string Original = "Set/first.flac";
    internal const string Proposed = "Organized/first.flac";
    internal const string Removed = "Set/missing.flac";

    internal sealed record Evidence(string[] Tracks, string[] Search, string[] ArtistMembers, string[] AlbumMembers, string[] PlaylistMembers,
        string? ProposedPath, TrackAvailability PlaybackAvailability, string? PlaybackPath, TrackAvailability RemovedAvailability,
        string? RemovedPlaybackPath, bool FeedRevisionPreserved, string BeforeStageCursor, string BeforeRevertCursor,
        string[] AnotherDesktopTracks, bool RestartRetainedEffects, string[] RevertedTracks, string? RevertedPath, bool BaseAndFilesUnchanged);

    /// <summary>Verifies exact local effects, playback, cursor invalidation, isolation, and restoration.</summary>
    internal static void Verify(Evidence value)
    {
        Assert.Equal(["First"], value.Tracks);
        Assert.Empty(value.Search);
        Assert.Equal(["First"], value.ArtistMembers);
        Assert.Equal(["First"], value.AlbumMembers);
        Assert.Equal(["First", "First"], value.PlaylistMembers);
        Assert.Equal(Proposed, value.ProposedPath);
        Assert.Equal(TrackAvailability.Downloaded, value.PlaybackAvailability);
        Assert.Equal(Original, value.PlaybackPath);
        Assert.Equal(TrackAvailability.Unknown, value.RemovedAvailability);
        Assert.Null(value.RemovedPlaybackPath);
        Assert.True(value.FeedRevisionPreserved);
        Assert.Equal("stale-catalog-cursor", value.BeforeStageCursor);
        Assert.Equal("stale-catalog-cursor", value.BeforeRevertCursor);
        Assert.Equal(["First", "Second"], value.AnotherDesktopTracks);
        Assert.True(value.RestartRetainedEffects);
        Assert.Equal(["First", "Second"], value.RevertedTracks);
        Assert.Equal(Original, value.RevertedPath);
        Assert.True(value.BaseAndFilesUnchanged);
    }
}
