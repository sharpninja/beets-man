using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using SoundFlow.Abstracts;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Codecs.FFMpeg;
using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Structs;
using NativePlaybackState = SoundFlow.Enums.PlaybackState;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Owns the two local SoundFlow players attached to the supplied audio mixer.</summary>
/// <remarks>2026-11-24: Bind FR-PLAYER-001#ac-transport and ac-mix to SoundFlow, MiniAudio, and FFmpeg under TR-PLAYER-ARCH-001.</remarks>
public sealed class SoundFlowDecks(AudioEngine engine, Mixer mixer, AudioFormat format, Action abandonAutomaticFade)
    : IPlaybackSeekTarget, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<DeckId, LoadedDeck> decks = [];
    private bool disposed;

    /// <summary>Creates the MiniAudio engine with the application's required FFmpeg codec extension.</summary>
    public static MiniAudioEngine CreateEngine()
    {
        var engine = new MiniAudioEngine();
        try
        {
            var ffmpeg = new FFmpegCodecFactory();
            engine.RegisterCodecFactory(ffmpeg);
            engine.SetCodecPriority(ffmpeg.FactoryId, -100);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    /// <summary>Loads a resolved local file without changing the existing deck when preparation fails.</summary>
    public Task<Outcome<DeckState>> PrepareAsync(DeckId deck, Guid trackId, string localPath, CancellationToken token) =>
        PrepareAsync(deck, trackId, localPath, TimeSpan.Zero, TransportStatus.Stopped, token);

    /// <summary>Loads and positions a new deck before replacing the current player.</summary>
    /// <remarks>2026-11-24: Prepare an incoming track before touching the outgoing track for FR-PLAYER-001#ac-transport and ac-fade.</remarks>
    public Task<Outcome<DeckState>> PrepareAsync(DeckId deck, Guid trackId, string localPath, TimeSpan position,
        TransportStatus status, CancellationToken token) =>
        InvokeAsync(() =>
        {
            if (!Enum.IsDefined(deck) || !Enum.IsDefined(status) || trackId == Guid.Empty || !Path.IsPathFullyQualified(localPath) || position < TimeSpan.Zero)
                return new Outcome<DeckState>.Invalid([new("invalid-deck-source")]);
            var player = OpenPlayer(localPath);
            var installed = false;
            try
            {
                if (position > TimeSpan.Zero)
                {
                    var resumePosition = player.Duration > 0 && position.TotalSeconds > player.Duration
                        ? TimeSpan.FromSeconds(player.Duration) : position;
                    if (!player.Seek(resumePosition))
                        return new Outcome<DeckState>.DependencyFailure("decoder-seek-failed", false);
                }
                token.ThrowIfCancellationRequested();
                if (status == TransportStatus.Paused)
                {
                    player.Play();
                    player.Pause();
                }
                else if (status == TransportStatus.Playing)
                    player.Play();
                mixer.AddComponent(player);
                installed = true;
            }
            finally
            {
                if (!installed)
                    player.Dispose();
            }
            if (decks.TryGetValue(deck, out var previous))
                ClosePlayer(previous.Player);
            var loaded = new LoadedDeck(trackId, player);
            decks[deck] = loaded;
            if (status == TransportStatus.Playing)
                abandonAutomaticFade();
            return new Outcome<DeckState>.Success(Snapshot(deck, loaded));
        }, token);

    /// <summary>Applies an explicit transport state to an already prepared deck.</summary>
    public Task<Outcome<DeckState>> ChangeStatusAsync(DeckId deck, TransportStatus status, CancellationToken token) =>
        ChangeStatusAsync(deck, null, status, token);

    /// <summary>Applies transport to the captured track occupying an already prepared deck.</summary>
    public Task<Outcome<DeckState>> ChangeStatusAsync(DeckId deck, Guid? expectedTrackId, TransportStatus status, CancellationToken token) =>
        InvokeAsync(() =>
        {
            if (!decks.TryGetValue(deck, out var loaded))
                return new Outcome<DeckState>.Invalid([new("deck-not-prepared")]);
            if (expectedTrackId is { } trackId && loaded.TrackId != trackId)
                return new Outcome<DeckState>.Conflict("transport-track-changed", null);
            switch (status)
            {
                case TransportStatus.Playing: loaded.Player.Play(); break;
                case TransportStatus.Paused: loaded.Player.Pause(); break;
                case TransportStatus.Stopped:
                    loaded.Player.Stop();
                    abandonAutomaticFade();
                    break;
                default: return new Outcome<DeckState>.Invalid([new("invalid-deck-status")]);
            }
            return new Outcome<DeckState>.Success(Snapshot(deck, loaded));
        }, token);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken cancellationToken) =>
        InvokeAsync(() =>
        {
            if (!decks.TryGetValue(deck, out var loaded) || loaded.TrackId != trackId)
                return new Outcome<DeckState>.Conflict("seek-track-changed", null);
            if (position < TimeSpan.Zero || loaded.Player.Duration > 0 && position.TotalSeconds > loaded.Player.Duration)
                return new Outcome<DeckState>.Invalid([new("seek-position-out-of-range")]);
            if (!loaded.Player.Seek(position))
                return new Outcome<DeckState>.DependencyFailure("decoder-seek-failed", false);
            abandonAutomaticFade();
            return new Outcome<DeckState>.Success(Snapshot(deck, loaded));
        }, cancellationToken);

    /// <summary>Reads the current native position while keeping the requested deck identity stable.</summary>
    /// <remarks>2026-11-24: Expose current native transport time for FR-PLAYER-001#ac-transport.</remarks>
    public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken cancellationToken) =>
        InvokeAsync(() => decks.TryGetValue(deck, out var loaded) && loaded.TrackId == trackId
            ? new Outcome<DeckState>.Success(Snapshot(deck, loaded))
            : new Outcome<DeckState>.Conflict("transport-track-changed", null), cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            foreach (var deck in decks.Values)
                ClosePlayer(deck.Player);
            decks.Clear();
            disposed = true;
        }
    }

    private async Task<Outcome<DeckState>> InvokeAsync(Func<Outcome<DeckState>> operation, CancellationToken token)
    {
        try
        {
            return await Task.Run(() =>
            {
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return operation();
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new Outcome<DeckState>.Cancelled();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return new Outcome<DeckState>.DependencyFailure("audio-backend-error", false);
        }
    }

    private SoundPlayer OpenPlayer(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            var provider = new StreamDataProvider(engine, format, stream);
            try { return new SoundPlayer(engine, format, provider); }
            catch { provider.Dispose(); throw; }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void ClosePlayer(SoundPlayer player)
    {
        mixer.RemoveComponent(player);
        player.Dispose();
    }

    private static DeckState Snapshot(DeckId deck, LoadedDeck loaded)
    {
        var player = loaded.Player;
        var status = player.State switch
        {
            NativePlaybackState.Playing => TransportStatus.Playing,
            NativePlaybackState.Paused => TransportStatus.Paused,
            _ => TransportStatus.Stopped
        };
        var duration = float.IsFinite(player.Duration) && player.Duration > 0 ? TimeSpan.FromSeconds(player.Duration) : (TimeSpan?)null;
        var position = float.IsFinite(player.Time) && player.Time > 0 ? TimeSpan.FromSeconds(player.Time) : TimeSpan.Zero;
        if (duration is { } end && position > end)
            position = end;
        return new(deck, loaded.TrackId, position, duration, status, true, player.Volume);
    }

    private sealed record LoadedDeck(Guid TrackId, SoundPlayer Player);
}
