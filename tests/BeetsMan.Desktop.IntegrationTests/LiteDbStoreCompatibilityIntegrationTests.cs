using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Opens older schema-one documents through the current desktop repository.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 to TR-RECORDS-ARCH-001#ac-litedb-c4-entities and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public sealed class LiteDbStoreCompatibilityIntegrationTests
{
    /// <summary>Preserves the exact active occurrence and position without starting audio when the persisted flag is absent.</summary>
    [Fact]
    public async Task MissingWasPlayingRestoresStopped()
    {
        var stop = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("beets-legacy-checkpoint-");
        try
        {
            var path = Path.Combine(root.FullName, "desktop.db");
            var track = Guid.NewGuid();
            var first = new QueueEntry(Guid.NewGuid(), track);
            var active = new QueueEntry(Guid.NewGuid(), track);
            var position = TimeSpan.FromSeconds(37);
            PlaybackCheckpoint saved;
            using (var writer = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
            {
                saved = new(writer.StoreId, 1, 4, [first, active], active.EntryId, position, false, RepeatMode.All,
                    new(new(Guid.NewGuid(), 11), "Recent list"), WasPlaying: true);
                Assert.IsType<Outcome<PlaybackCheckpoint>.Success>(await writer.SaveCheckpointAsync(saved, 0, stop));
            }

            using (var database = new LiteDatabase(path))
            {
                var rows = database.GetCollection<BsonDocument>("playback_sessions");
                var row = rows.FindById(saved.SessionId);
                Assert.True(row["WasPlaying"].AsBoolean);
                Assert.True(row.Remove("WasPlaying"));
                Assert.True(rows.Update(row));
                Assert.False(rows.FindById(saved.SessionId).ContainsKey("WasPlaying"));
            }

            using var reader = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
            var persisted = Assert.IsType<Outcome<PlaybackCheckpoint?>.Success>(
                await reader.ReadCheckpointAsync(saved.SessionId, stop)).Value;
            Assert.NotNull(persisted);
            Assert.False(persisted.WasPlaying);
            Assert.Equal(saved.PlaylistBacking, persisted.PlaylistBacking);

            var backend = new PlaybackRestartIntegrationTests.StartupBackend();
            var initial = new PlaybackState(reader.StoreId, 0, new(0, [], false),
                PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, TransportStatus.Stopped);
            var preference = new EnabledRestart();
            var dispatcher = new PlaybackSessionHandlers(initial, DeckId.A, backend, reader, restartPreference: preference)
                .Register(new RequestDispatcher.Builder()).Build();
            var restored = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(
                new InitializePlayback(reader.StoreId), new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), stop)).Value;
            Assert.Equal(TransportStatus.Stopped, restored.Status);
            Assert.Equal(active.EntryId, restored.ActiveQueueEntryId);
            Assert.Equal(position, restored.Position);
            Assert.Equal(saved.Queue.ToArray(), restored.Queue.Entries.ToArray());
            Assert.Equal(RepeatMode.All, restored.Queue.Repeat);
            Assert.Empty(backend.Starts);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Enables resume so a missing persisted flag is the condition that prevents startup playback.</summary>
    private sealed class EnabledRestart : IPlaybackRestartPreference
    {
        /// <inheritdoc />
        public Task<Outcome<bool>> ReadRestartPreferenceAsync(CancellationToken token) =>
            Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true));
    }
}
