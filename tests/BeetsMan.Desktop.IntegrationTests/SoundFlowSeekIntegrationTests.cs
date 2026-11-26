using BeetsMan.Desktop.Adapters.Playback;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Tests;
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

    /// <summary>Reopens a saved playlist checkpoint and resumes its exact entry through the native deck.</summary>
    /// <remarks>2026-11-25: Verify the real decoder and persistent opt-in for FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
    [Fact]
    public async Task RestartStartsTheSavedPlaylistOnTheNativeDeck()
    {
        Directory.CreateDirectory(root);
        WriteWave(Path.Combine(root, "sample.wav"));
        var databasePath = Path.Combine(root, "playback.db");
        var library = Guid.NewGuid();
        var session = Guid.NewGuid();
        var track = Guid.NewGuid();
        var first = new QueueEntry(Guid.NewGuid(), track);
        var active = new QueueEntry(Guid.NewGuid(), track);
        var position = TimeSpan.FromMilliseconds(800);
        var checkpoint = new PlaybackCheckpoint(session, 1, 2, [first, active], active.EntryId, position, false, RepeatMode.Off,
            new(new(Guid.NewGuid(), 4), "Native fixture"), WasPlaying: true);
        var token = TestContext.Current.CancellationToken;
        var request = new RequestContext(Guid.NewGuid(), null);
        using (var store = LiteDbDesktopPlaylistStore.Open(databasePath, true))
        {
            Assert.IsType<Outcome<PlaybackCheckpoint>.Success>(await store.SaveCheckpointAsync(checkpoint, 0, token));
            var settings = new PlaybackPreferenceHandlers(library, store).Register(new()).Build();
            Assert.IsType<Outcome<EffectivePreference>.Success>(await settings.SendAsync(
                new SetLocalOverride(PlaybackPreferences.ResumeAfterRestart, new PreferenceValue.Flag(true), 0),
                new(request, Guid.NewGuid()), token));
        }
        using (var fixture = new LiteDatabase(databasePath))
            fixture.GetCollection<BsonDocument>("catalog_tracks").Insert(new BsonDocument
            {
                ["_id"] = track, ["LibraryId"] = library, ["Title"] = "Fixture", ["RelativePath"] = "sample.wav"
            });

        using var reopened = LiteDbDesktopPlaylistStore.Open(databasePath, false);
        using var engine = SoundFlowDecks.CreateEngine();
        using var mixer = new Mixer(engine, AudioFormat.Dvd);
        using var decks = new SoundFlowDecks(engine, mixer, AudioFormat.Dvd, static () => { });
        var initial = new PlaybackState(session, 0, new(0, [], false),
            PlaybackCapabilities.Transport | PlaybackCapabilities.Seek, TransportStatus.Stopped);
        var preferences = new PlaybackPreferenceHandlers(library, reopened);
        var handlers = new PlaybackSessionHandlers(initial, DeckId.A,
            new SoundFlowPlaybackTransport(new(reopened, library, root, () => true), decks), reopened, restartPreference: preferences);
        var dispatcher = preferences.Register(handlers.Register(new RequestDispatcher.Builder())).Build();
        var resumed = Assert.IsType<Outcome<PlaybackState>.Success>(await dispatcher.SendAsync(
            new InitializePlayback(session), new(request, Guid.NewGuid()), token)).Value;
        Assert.Equal(TransportStatus.Playing, resumed.Status);
        Assert.Equal(active.EntryId, resumed.ActiveQueueEntryId);
        var deck = Assert.IsType<Outcome<DeckState>.Success>(await decks.ReadAsync(DeckId.A, track, token)).Value;
        Assert.Equal(TransportStatus.Playing, deck.Status);
        Assert.InRange(deck.Position.TotalMilliseconds, 799, 801);
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

    /// <summary>Measures actual two-deck gain and equalizer response while driving the playback CQRS commands.</summary>
    /// <remarks>2026-11-25: Bind FR-PLAYER-001#ac-mix to separate WAV tones, the SoundFlow mixer, and its master EQ.</remarks>
    [Fact]
    public async Task MixerCommandsChangeRenderedAudioAcrossBothDecksAndMasterEq()
    {
        const int aFrequency = 1000;
        const int bFrequency = 2000;
        Directory.CreateDirectory(root);
        WriteWave(Path.Combine(root, "a.wav"), aFrequency);
        WriteWave(Path.Combine(root, "b.wav"), bFrequency);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var library = Guid.NewGuid();
        var database = Path.Combine(root, "mix.db");
        using (LiteDbDesktopPlaylistStore.Open(database, true)) { }
        using (var setup = new LiteDatabase(database))
        {
            var tracks = setup.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(new BsonDocument { ["_id"] = a, ["LibraryId"] = library, ["RelativePath"] = "a.wav", ["Title"] = "A" });
            tracks.Insert(new BsonDocument { ["_id"] = b, ["LibraryId"] = library, ["RelativePath"] = "b.wav", ["Title"] = "B" });
        }
        using var store = LiteDbDesktopPlaylistStore.Open(database, false);
        using var engine = SoundFlowDecks.CreateEngine();
        var format = AudioFormat.Dvd;
        using var mixer = new Mixer(engine, format);
        var cancels = 0;
        using var decks = new SoundFlowDecks(engine, mixer, format, () => cancels++);
        var target = new SoundFlowPlaybackTransport(new(store, library, root, () => true), decks);
        var session = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var request = new RequestContext(Guid.NewGuid(), null);
        var initial = new PlaybackState(session, 0, new(0, [], false), PlaybackCapabilities.Transport | PlaybackCapabilities.Seek |
            PlaybackCapabilities.Volume | PlaybackCapabilities.Mixer | PlaybackCapabilities.Equalizer, TransportStatus.Stopped);
        var dispatcher = new PlaybackSessionHandlers(initial, DeckId.A, target, store).Register(new()).Build();
        Value(await SendAsync(new EnqueueTracks(session, [a], EnqueueMode.PlayNow)));
        var cued = Value(await SendAsync(new CueDeck(session, DeckId.B, b)));
        Assert.Equal(TransportStatus.Stopped, cued.Decks.Single(deck => deck.DeckId == DeckId.B).Status);
        var playing = Value(await SendAsync(new Transport(session, TransportAction.Play, DeckId.B)));
        var left = Render();
        var priorCancels = cancels;
        Value(await SendAsync(new SetMix(session, new MixEdit.CrossfaderPosition(1))));
        var right = Render();
        var abandoned = cancels == priorCancels + 1;
        Value(await SendAsync(new SetMix(session, new MixEdit.CrossfaderPosition(0.5))));
        var midpoint = Render();
        var level = Value(await SendAsync(new SetMix(session, new MixEdit.DeckGain(DeckId.B, 0.4))));
        Assert.Equal(0.4, level.Decks.Single(deck => deck.DeckId == DeckId.B).Gain);
        var tuned = Render();
        Value(await SendAsync(new SetMix(session, new MixEdit.MasterGain(0.5))));
        var half = Render();
        Value(await SendAsync(new SetMix(session, new MixEdit.CrossfaderPosition(0))));
        var baseline = Render();
        var boosted = Value(await SendAsync(new SetEqualizer(session, new EqualizerEdit.ApplyBands([0, 0, 0, 0, 0, 6, 0, 0, 0, 0]))));
        Assert.Equal(6, boosted.Output!.EqualizerBands[5]);
        var equalized = Render();
        var bypassed = Value(await SendAsync(new SetEqualizer(session, new EqualizerEdit.Bypass(true))));
        Assert.True(bypassed.Output!.EqualizerBypassed);
        Assert.Equal(6, bypassed.Output.EqualizerBands[5]);
        var bypass = Render();
        Value(await SendAsync(new SetEqualizer(session, new EqualizerEdit.Bypass(false))));
        var reset = Value(await SendAsync(new SetEqualizer(session, new EqualizerEdit.ResetBands())));
        var flat = Render();
        var invalidGain = await SendAsync(new SetMix(session, new MixEdit.MasterGain(double.NaN)));
        var invalidDeck = await SendAsync(new SetMix(session, new MixEdit.DeckGain((DeckId)71, 0.4)));
        var invalidBands = await SendAsync(new SetEqualizer(session, new EqualizerEdit.ApplyBands([0, 0, 0, 0, 0, 13, 0, 0, 0, 0])));
        var final = Value(await dispatcher.QueryAsync(new GetPlaybackState(session), request, token));
        var untouched = invalidGain is Outcome<PlaybackState>.Invalid && invalidDeck is Outcome<PlaybackState>.Invalid &&
            invalidBands is Outcome<PlaybackState>.Invalid && final.Revision == reset.Revision && final.Output == reset.Output;

        Assert.True(left.A > 0.03 && right.B > 0.03);
        PlaybackMixAssertions.Verify(new(playing.Decks.Length == 2 && playing.Decks.All(deck => deck.Status == TransportStatus.Playing),
            left.B / right.B, right.A / left.A, midpoint.A / left.A, midpoint.B / right.B, tuned.B / midpoint.B,
            tuned.A / midpoint.A, half.A / tuned.A, half.B / tuned.B, equalized.A / baseline.A, bypass.A / baseline.A,
            flat.A / baseline.A, abandoned, untouched));

        Task<Outcome<PlaybackState>> SendAsync(ICommand<PlaybackState> command) => dispatcher.SendAsync(command, new(request, Guid.NewGuid()), token);
        static PlaybackState Value(Outcome<PlaybackState> result) => Assert.IsType<Outcome<PlaybackState>.Success>(result).Value;
        (double A, double B) Render()
        {
            var frames = format.SampleRate / 10;
            var samples = new float[frames * format.Channels];
            mixer.Process(samples, format.Channels);
            return (Amplitude(aFrequency), Amplitude(bFrequency));

            double Amplitude(int frequency)
            {
                var discard = format.SampleRate / 100;
                double real = 0, imaginary = 0;
                for (var frame = discard; frame < frames; frame++)
                {
                    var angle = 2 * Math.PI * frequency * frame / format.SampleRate;
                    var sample = samples[frame * format.Channels];
                    real += sample * Math.Cos(angle);
                    imaginary += sample * Math.Sin(angle);
                }
                return 2 * Math.Sqrt(real * real + imaginary * imaginary) / (frames - discard);
            }
        }
    }

    /// <summary>Reads bounded telemetry from a real SoundFlow render and discards spent windows.</summary>
    /// <remarks>2026-11-25: Exercise FR-PLAYER-001#ac-modes and FR-PLUGINS-001#ac-visualize with a known native tone.</remarks>
    [Fact]
    public async Task CapturesNativeWaveformAndSpectrumWithoutReplayingOldFrames()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "visualization.wav");
        WriteWave(path, frequency: 1500, amplitude: 24500);
        using var engine = SoundFlowDecks.CreateEngine();
        var format = AudioFormat.Dvd;
        using var mixer = new Mixer(engine, format);
        using var decks = new SoundFlowDecks(engine, mixer, format, static () => { });
        var clock = new VisualizationClock();
        var analyzer = new SoundFlowVisualizationCapture(format, clock);
        mixer.AddAnalyzer(analyzer);
        var before = analyzer.ReadLatest(0);
        Assert.IsType<Outcome<DeckState>.Success>(await decks.PrepareAsync(DeckId.A, Guid.NewGuid(), path,
            TimeSpan.Zero, TransportStatus.Playing, TestContext.Current.CancellationToken));
        var buffer = new float[256 * format.Channels];
        for (var block = 0; block < 4; block++)
            mixer.Process(buffer, format.Channels);
        var live = Assert.IsType<PlaybackVisualizationFrame>(analyzer.ReadLatest(0));
        Assert.Equal(48000, live.SampleRate);
        var duplicate = analyzer.ReadLatest(live.Sequence);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var expired = analyzer.ReadLatest(0);
        AudioVisualizationAssertions.Verify(Observe(before), Observe(live), Observe(duplicate), Observe(expired));
        Assert.IsType<Outcome<bool>.Success>(await decks.ApplyMixAsync(PlaybackMixSettings.Default with { MasterGain = 0.5 },
            false, TestContext.Current.CancellationToken));
        mixer.Process(buffer, format.Channels);
        Assert.Null(analyzer.ReadLatest(live.Sequence));
        for (var block = 0; block < 3; block++)
            mixer.Process(buffer, format.Channels);
        var quieter = Assert.IsType<PlaybackVisualizationFrame>(analyzer.ReadLatest(live.Sequence));
        Assert.True(quieter.Sequence > live.Sequence);
        Assert.InRange(quieter.Spectrum[17] / live.Spectrum[17], 0.49f, 0.51f);

        static AudioVisualizationAssertions.Observation? Observe(PlaybackVisualizationFrame? frame) =>
            frame is null ? null : new(frame.Sequence, frame.Waveform, frame.Spectrum);
    }

    private sealed class VisualizationClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }

    private static void WriteWave(string path, int frequency = 0, int amplitude = 6000)
    {
        const int sampleRate = 48000;
        const int bytesPerSample = 2;
        const int dataBytes = 2 * sampleRate * bytesPerSample;
        using var output = new BinaryWriter(File.Create(path));
        output.Write("RIFF"u8); output.Write(36 + dataBytes); output.Write("WAVE"u8);
        output.Write("fmt "u8); output.Write(16); output.Write((short)1); output.Write((short)1);
        output.Write(sampleRate); output.Write(sampleRate * bytesPerSample); output.Write((short)bytesPerSample); output.Write((short)16);
        output.Write("data"u8); output.Write(dataBytes);
        for (var sample = 0; sample < dataBytes / bytesPerSample; sample++)
            output.Write((short)(amplitude * Math.Sin(2 * Math.PI * frequency * sample / sampleRate)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
