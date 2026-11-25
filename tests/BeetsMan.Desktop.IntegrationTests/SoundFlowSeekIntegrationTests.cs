using BeetsMan.Desktop.Adapters.Playback;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using LiteDB;
using SoundFlow.Components;
using SoundFlow.Structs;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises file decoding, initial transport state, and seeking with the native audio library.</summary>
/// <remarks>2026-11-24: Bind a real MiniAudio/FFmpeg mixer and decoder for TEST-PLAYER-002 and FR-PLAYER-001#ac-transport.</remarks>
public sealed class SoundFlowSeekIntegrationTests : IDisposable
{
    // Two seconds of stereo silence from an independent AAC encoder at 48 kHz.
    private const string AacSilence =
        "//FMgAOf/NwATGF2YzYzLjEuMTAyAEIgCMEYOP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIAB" +
        "v/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCM" +
        "HP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG/" +
        "/CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc" +
        "//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8" +
        "IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/" +
        "8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/wh" +
        "EARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/x" +
        "TIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQ" +
        "BGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FM" +
        "gAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAE" +
        "YIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyA" +
        "Ab/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARg" +
        "jBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIAB" +
        "v/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCM" +
        "HP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG/" +
        "/CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc//FMgAG//CEQBGCMHP/xTIABv/whEARgjBz/8UyAAb/8IRAEYIwc" +
        "//FMgAG//CEQBGCMHP/xTIABv/whEARgjBw=";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-man-native-audio-{Guid.NewGuid():N}");

    [Fact]
    public async Task APreparedWaveSeeksWithoutReplacingTheOtherDeck()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.wav");
        WriteWave(path);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var engine = SoundFlowDecks.CreateEngine();
        using var mixer = new Mixer(engine, AudioFormat.Dvd);
        var fadesAbandoned = 0;
        using var decks = new SoundFlowDecks(engine, mixer, AudioFormat.Dvd, () => fadesAbandoned++);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var prepared = Assert.IsType<Outcome<DeckState>.Success>(await decks.PrepareAsync(DeckId.A, first, path, cancellationToken)).Value;
        Assert.InRange(prepared.Duration!.Value.TotalSeconds, 1.99, 2.01);
        Assert.IsType<Outcome<DeckState>.Success>(await decks.PrepareAsync(DeckId.B, second, path, cancellationToken));
        Assert.IsType<Outcome<DeckState>.DependencyFailure>(
            await decks.PrepareAsync(DeckId.A, Guid.NewGuid(), Path.Combine(root, "missing.wav"), cancellationToken));

        var result = Assert.IsType<Outcome<DeckState>.Success>(await decks.SeekAsync(DeckId.A, first, TimeSpan.FromSeconds(1), cancellationToken)).Value;
        Assert.Equal(DeckId.A, result.DeckId);
        Assert.Equal(first, result.TrackId);
        Assert.InRange(result.Position.TotalSeconds, 0.999, 1.001);
        Assert.Equal(1, fadesAbandoned);
        Assert.IsType<Outcome<DeckState>.Conflict>(await decks.SeekAsync(DeckId.B, first, TimeSpan.Zero, cancellationToken));
        Assert.Equal(1, fadesAbandoned);
        Assert.IsType<Outcome<DeckState>.Success>(await decks.SeekAsync(DeckId.B, second, TimeSpan.Zero, cancellationToken));
    }

    [Fact]
    public async Task FfmpegOnlyAudioUsesTheSameSeekCoordinate()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.aac");
        var format = AudioFormat.Dvd;
        File.WriteAllBytes(path, Convert.FromBase64String(AacSilence));

        using var engine = SoundFlowDecks.CreateEngine();
        using var mixer = new Mixer(engine, format);
        using var decks = new SoundFlowDecks(engine, mixer, format, static () => { });
        var track = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var prepared = Assert.IsType<Outcome<DeckState>.Success>(await decks.PrepareAsync(DeckId.A, track, path, token)).Value;
        Assert.InRange(prepared.Duration!.Value.TotalSeconds, 1.90, 2.10);
        var result = Assert.IsType<Outcome<DeckState>.Success>(await decks.SeekAsync(DeckId.A, track, TimeSpan.FromSeconds(1), token)).Value;
        Assert.InRange(result.Position.TotalSeconds, 0.99, 1.01);
    }

    [Fact]
    public async Task CqrsTransportUsesDownloadedMediaAndResumesTheRealCheckpoint()
    {
        Directory.CreateDirectory(root);
        WriteWave(Path.Combine(root, "sample.wav"));
        var library = Guid.NewGuid();
        var track = Guid.NewGuid();
        var databasePath = Path.Combine(root, "playback.db");
        using (LiteDbDesktopPlaylistStore.Open(databasePath, true)) { }
        using (var fixture = new LiteDatabase(databasePath))
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
            {
                ["_id"] = track, ["LibraryId"] = library, ["Title"] = "Fixture", ["RelativePath"] = "sample.wav"
            });

        var session = Guid.NewGuid();
        var initial = new PlaybackState(session, 0, new(0, [], false),
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, TransportStatus.Stopped);
        var token = TestContext.Current.CancellationToken;
        var request = new RequestContext(Guid.NewGuid(), null);
        var target = TimeSpan.FromMilliseconds(500);
        Guid? activeEntry;
        using var engine = SoundFlowDecks.CreateEngine();
        using var mixer = new Mixer(engine, AudioFormat.Dvd);
        using (var store = LiteDbDesktopPlaylistStore.Open(databasePath, false))
        using (var decks = new SoundFlowDecks(engine, mixer, AudioFormat.Dvd, static () => { }))
        {
            var transport = new SoundFlowPlaybackTransport(new(store, library, root, () => true), decks);
            var dispatcher = new PlaybackSessionHandlers(initial, DeckId.A, transport, store).Register(new RequestDispatcher.Builder()).Build();
            var started = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(
                new EnqueueTracks(session, [track, track], EnqueueMode.PlayNow), new(request, Guid.NewGuid()), token)).Value;
            Assert.Equal(TransportStatus.Playing, started.Status);
            activeEntry = started.ActiveQueueEntryId;
            Assert.Equal(started.Queue.Entries[0].EntryId, activeEntry);
            var seek = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(
                new Seek(session, track, target), new(request, Guid.NewGuid()), token)).Value;
            Assert.InRange(seek.Position.TotalMilliseconds, 499, 501);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(databasePath, false);
        using var freshDecks = new SoundFlowDecks(engine, mixer, AudioFormat.Dvd, static () => { });
        var resumed = new PlaybackSessionHandlers(initial, DeckId.A,
            new SoundFlowPlaybackTransport(new(reopened, library, root, () => true), freshDecks), reopened)
            .Register(new RequestDispatcher.Builder()).Build();
        var restored = Assert.IsType<Outcome<PlaybackState>.Success>(await resumed.QueryAsync(new GetPlaybackState(session), request, token)).Value;
        Assert.Equal(activeEntry, restored.ActiveQueueEntryId);
        Assert.Equal(TransportStatus.Stopped, restored.Status);
        Assert.InRange(restored.Position.TotalMilliseconds, 499, 501);
        var play = Assert.IsType<Outcome<PlaybackState>.Success>(await resumed.SendAsync(
            new Transport(session, TransportAction.Play), new(request, Guid.NewGuid()), token)).Value;
        Assert.Equal(TransportStatus.Playing, play.Status);
        Assert.Equal(activeEntry, play.ActiveQueueEntryId);
        Assert.InRange(play.Position.TotalMilliseconds, 499, 501);
    }

    [Fact]
    public async Task RenderingNativeAudioAdvancesTheReportedPosition()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.wav");
        WriteWave(path);
        using var engine = SoundFlowDecks.CreateEngine();
        var format = AudioFormat.Dvd;
        using var mixer = new Mixer(engine, format);
        using var decks = new SoundFlowDecks(engine, mixer, format, static () => { });
        var track = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var started = Assert.IsType<Outcome<DeckState>.Success>(
            await decks.PrepareAsync(DeckId.A, track, path, TimeSpan.Zero, TransportStatus.Playing, token)).Value;
        var buffer = new float[format.SampleRate * format.Channels / 10];
        mixer.Process(buffer, format.Channels);
        var current = Assert.IsType<Outcome<DeckState>.Success>(await decks.ReadAsync(DeckId.A, track, token)).Value;
        Assert.InRange((current.Position - started.Position).TotalMilliseconds, 99, 101);
        Assert.Equal(TransportStatus.Playing, current.Status);
    }

    private static void WriteWave(string path)
    {
        const int sampleRate = 48000;
        const int bytesPerSample = 2;
        const int dataBytes = 2 * sampleRate * bytesPerSample;
        using var output = new BinaryWriter(File.Create(path));
        output.Write("RIFF"u8); output.Write(36 + dataBytes); output.Write("WAVE"u8);
        output.Write("fmt "u8); output.Write(16); output.Write((short)1); output.Write((short)1);
        output.Write(sampleRate); output.Write(sampleRate * bytesPerSample); output.Write((short)bytesPerSample); output.Write((short)16);
        output.Write("data"u8); output.Write(dataBytes); output.Write(new byte[dataBytes]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
