using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using static BeetsMan.Desktop.Tests.PlaybackCheckpointAssertions;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Checks durable playback state and rejection of stale writes using LiteDB.</summary>
/// <remarks>2026-11-24: Bind TEST-PLAYER-001/002 persistence assertions for FR-PLAYER-001#ac-transport.</remarks>
public sealed class PlaybackCheckpointIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-man-playback-{Guid.NewGuid():N}");
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckpointPersistsRepeatOccurrencesAndPositionAcrossReopen()
    {
        var path = Path.Combine(root, "playback.db");
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var newSession = Assert.IsType<Outcome<PlaybackCheckpoint?>.Success>(await store.ReadCheckpointAsync(Session, Token));
            Assert.Null(newSession.Value);
            var initial = new PlaybackCheckpoint(Session, 1, 3, [], null, TimeSpan.Zero, false, RepeatMode.Off);
            Assert.IsType<Outcome<PlaybackCheckpoint>.Success>(await store.SaveCheckpointAsync(initial, 0, Token));
            var next = new PlaybackCheckpoint(Session, 2, 4, Entries, Entries[1].EntryId, TimeSpan.FromSeconds(42), true, RepeatMode.One);
            Assert.IsType<Outcome<PlaybackCheckpoint>.Success>(await store.SaveCheckpointAsync(next, 1, Token));
            var stale = Assert.IsType<Outcome<PlaybackCheckpoint>.Conflict>(
                await store.SaveCheckpointAsync(next with { Position = TimeSpan.Zero }, 1, Token));
            Assert.Equal("stale-playback-checkpoint", stale.Code);
            var mismatched = next with { CheckpointRevision = 3, Queue = Entries.Reverse().ToImmutableArray() };
            Assert.IsType<Outcome<PlaybackCheckpoint>.Conflict>(await store.SaveCheckpointAsync(mismatched, 2, Token));
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var loaded = Assert.IsType<Outcome<PlaybackCheckpoint?>.Success>(await reopened.ReadCheckpointAsync(Session, Token)).Value;
        Assert.NotNull(loaded);
        Verify(loaded.SessionId, loaded.CheckpointRevision, loaded.QueueRevision, loaded.Queue, loaded.ActiveEntryId, loaded.Position, loaded.Shuffle, loaded.Repeat);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
