using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Exceptions;
using SoundFlow.Structs;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Routes the desktop's SoundFlow decks through the default system playback device.</summary>
/// <remarks>2026-11-25: Initialize native output on first use and release it after checkpointing for FR-PLAYER-001#ac-transport and ac-resume-playback-after-restart.</remarks>
/// <remarks>2026-11-25: Retain mixer controls before creating the system output for FR-PLAYER-001#ac-mix.</remarks>
/// <remarks>2026-11-26: Exercise native-engine initialization failures for FR-PLAYER-001#ac-transport and FR-CLI-001#ac-switch.</remarks>
internal sealed class SoundFlowPlaybackOutput(LiteDbCatalogReader catalog, Func<MiniAudioEngine>? createEngine = null)
    : IDesktopPlaybackOutput, IPlaybackMixTarget, IPlaybackVisualizationSource
{
    private OutputGraph? graph;
    private PlaybackMixSettings initialMix = PlaybackMixSettings.Default;
    private bool disposed;

    /// <inheritdoc />
    public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? position, TransportStatus status, CancellationToken token) =>
        UseAsync(output => output.ChangeAsync(deck, trackId, position, status, token), token);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) =>
        UseAsync(output => output.ReadAsync(deck, trackId, token), token);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
        UseAsync(output => output.SeekAsync(deck, trackId, position, token), token);

    /// <inheritdoc />
    /// <remarks>2026-11-25: Expose FR-PLAYER-001#ac-modes telemetry without initializing the device for a read.</remarks>
    public PlaybackVisualizationFrame? ReadLatest(long afterSequence) =>
        Volatile.Read(ref graph)?.Visualization.ReadLatest(afterSequence);

    /// <inheritdoc />
    public Task<Outcome<bool>> ApplyMixAsync(PlaybackMixSettings settings, bool abandonAutomaticFade, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (token.IsCancellationRequested)
            return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Cancelled());
        if (graph is { } active)
            return active.Transport.ApplyMixAsync(settings, abandonAutomaticFade, token);
        if (!settings.IsValid())
            return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Invalid([new("invalid-audio-mix")]));
        initialMix = settings;
        return Task.FromResult<Outcome<bool>>(new Outcome<bool>.Success(true));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        graph?.Dispose();
        graph = null;
    }

    private Task<Outcome<DeckState>> UseAsync(Func<SoundFlowPlaybackTransport, Task<Outcome<DeckState>>> operation, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (token.IsCancellationRequested)
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Cancelled());
        try { graph ??= CreateGraph(); }
        catch (Exception exception) when (exception is BackendException or MiniaudioException or DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.DependencyFailure("audio-output-unavailable", true));
        }
        return operation(graph.Transport);
    }

    private OutputGraph CreateGraph()
    {
        var engine = (createEngine ?? SoundFlowDecks.CreateEngine)();
        AudioPlaybackDevice? device = null;
        SoundFlowDecks? decks = null;
        try
        {
            device = engine.InitializePlaybackDevice(null, AudioFormat.Dvd);
            decks = new SoundFlowDecks(engine, device.MasterMixer, device.Format, static () => { }, initialMix);
            var visualization = new SoundFlowVisualizationCapture(device.Format);
            device.MasterMixer.AddAnalyzer(visualization);
            device.Start();
            return new(engine, device, decks, new(catalog, decks), visualization);
        }
        catch
        {
            Release(engine, device, decks);
            throw;
        }
    }

    private static void Release(AudioEngine engine, AudioPlaybackDevice? device, SoundFlowDecks? decks)
    {
        try { device?.Stop(); }
        finally
        {
            try { decks?.Dispose(); }
            finally
            {
                try { device?.Dispose(); }
                finally { engine.Dispose(); }
            }
        }
    }

    private sealed record OutputGraph(AudioEngine Engine, AudioPlaybackDevice Device, SoundFlowDecks Decks,
        SoundFlowPlaybackTransport Transport, SoundFlowVisualizationCapture Visualization) : IDisposable
    {
        public void Dispose() => Release(Engine, Device, Decks);
    }
}
