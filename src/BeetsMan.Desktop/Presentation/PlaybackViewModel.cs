using System.Collections.Immutable;
using System.ComponentModel;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies the original track and selected position at the end of a seek gesture.</summary>
public sealed record SeekGesture(Guid TrackId, TimeSpan Position, DeckId? DeckId = null);

/// <summary>Contains the track selected for an independent deck.</summary>
public sealed record DeckCue(DeckId DeckId, Guid TrackId);

/// <summary>Contains the requested gain for an individual deck.</summary>
public sealed record DeckGainInput(DeckId DeckId, double Gain);

/// <summary>Owns transport, seek, and mixer commands for a shared playback session.</summary>
/// <remarks>2026-11-24: Bind FR-PLAYER-001#ac-transport, ac-mix and ac-fade without changing the audio backend.</remarks>
public sealed partial class PlaybackViewModel : PresentationViewModel
{
    private readonly PlaybackPresentation playback;
    private double? pendingCrossfader;
    private bool sendingCrossfader;

    /// <summary>Creates the transport ViewModel over a shared session.</summary>
    public PlaybackViewModel(PresentationServices services, PlaybackPresentation playback) : base(services, playback.SessionId)
    {
        this.playback = playback;
        playback.PropertyChanged += OnPlaybackChanged;
    }

    /// <summary>Gets the latest snapshot independent of window mode.</summary>
    public PlaybackState? State => playback.State;

    [RelayCommand]
    private Task LoadAsync(CancellationToken token) =>
        ReadAsync(new GetPlaybackState(playback.SessionId), "playback", playback.Apply, token, playback.Accepts);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task PlayPauseAsync(CancellationToken token) => TransportAsync(
        State?.Status == TransportStatus.Playing ? TransportAction.Pause : TransportAction.Play, null, token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task PauseAsync(CancellationToken token) => TransportAsync(TransportAction.Pause, null, token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task NextAsync(CancellationToken token) => TransportAsync(TransportAction.Next, null, token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task PreviousAsync(CancellationToken token) => TransportAsync(TransportAction.Previous, null, token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task StopAsync(CancellationToken token) => TransportAsync(TransportAction.Stop, null, token);

    [RelayCommand(CanExecute = nameof(CanMix))]
    private Task PlayDeckAsync(DeckId deck, CancellationToken token) => TransportAsync(TransportAction.Play, deck, token);

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private Task CommitSeekAsync(SeekGesture input, CancellationToken token) => CanSeek(input)
        ? Send(new Seek(playback.SessionId, input.TrackId, input.Position, input.DeckId), token) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanMix))]
    private Task CueDeckAsync(DeckCue input, CancellationToken token) =>
        Send(new CueDeck(playback.SessionId, input.DeckId, input.TrackId), token);

    [RelayCommand(CanExecute = nameof(CanVolume))]
    private Task SetVolumeAsync(double gain, CancellationToken token) => MixAsync(new MixEdit.MasterGain(gain), token);

    [RelayCommand(CanExecute = nameof(CanMix))]
    private Task SetDeckGainAsync(DeckGainInput input, CancellationToken token) => MixAsync(new MixEdit.DeckGain(input.DeckId, input.Gain), token);

    [RelayCommand(CanExecute = nameof(CanMoveCrossfader), AllowConcurrentExecutions = true)]
    private async Task MoveCrossfaderAsync(double position)
    {
        if (!CanMoveCrossfader())
            return;
        pendingCrossfader = position;
        if (sendingCrossfader)
            return;

        sendingCrossfader = true;
        try
        {
            while (IsActive && pendingCrossfader is { } next)
            {
                pendingCrossfader = null;
                await MixAsync(new MixEdit.CrossfaderPosition(next), CancellationToken.None);
            }
        }
        finally
        {
            sendingCrossfader = false;
            pendingCrossfader = null;
            MoveCrossfaderCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMix))]
    private Task ToggleAutoFadeAsync(bool enabled, CancellationToken token) => MixAsync(new MixEdit.AutoFadeEnabled(enabled), token);

    [RelayCommand(CanExecute = nameof(CanEqualize))]
    private Task ApplyEqualizerAsync(ImmutableArray<double> bands, CancellationToken token) => EqAsync(new EqualizerEdit.ApplyBands(bands), token);

    [RelayCommand(CanExecute = nameof(CanEqualize))]
    private Task ResetEqualizerAsync(CancellationToken token) => EqAsync(new EqualizerEdit.ResetBands(), token);

    [RelayCommand(CanExecute = nameof(CanEqualize))]
    private Task SetEqBypassAsync(bool enabled, CancellationToken token) => EqAsync(new EqualizerEdit.Bypass(enabled), token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task SetShuffleAsync(bool enabled, CancellationToken token) =>
        Send(new SetQueuePlaybackOptions(playback.SessionId, new QueuePlaybackEdit.Shuffle(enabled)), token);

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private Task SetRepeatAsync(RepeatMode mode, CancellationToken token) =>
        Send(new SetQueuePlaybackOptions(playback.SessionId, new QueuePlaybackEdit.Repeat(mode)), token);

    /// <summary>Projects a current application event through the UI scheduler.</summary>
    public Task ReceiveAsync(PlaybackState incoming) => PostAsync(Scope, () =>
    {
        if (playback.Accepts(incoming))
            playback.Apply(incoming);
    });

    protected override void OnDisposed() => playback.PropertyChanged -= OnPlaybackChanged;

    protected override void OnWriteAvailabilityChanged()
    {
        PlayPauseCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged();
        PlayDeckCommand.NotifyCanExecuteChanged(); CueDeckCommand.NotifyCanExecuteChanged(); CommitSeekCommand.NotifyCanExecuteChanged();
        SetVolumeCommand.NotifyCanExecuteChanged(); SetDeckGainCommand.NotifyCanExecuteChanged(); MoveCrossfaderCommand.NotifyCanExecuteChanged();
        ToggleAutoFadeCommand.NotifyCanExecuteChanged(); ApplyEqualizerCommand.NotifyCanExecuteChanged(); ResetEqualizerCommand.NotifyCanExecuteChanged();
        SetEqBypassCommand.NotifyCanExecuteChanged(); SetShuffleCommand.NotifyCanExecuteChanged(); SetRepeatCommand.NotifyCanExecuteChanged();
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(State));
        OnWriteAvailabilityChanged();
    }

    private bool CanTransport() => CanWrite && Supports(PlaybackCapabilities.Transport);
    private bool CanVolume() => CanWrite && Supports(PlaybackCapabilities.Volume);
    private bool CanMix() => CanWrite && Supports(PlaybackCapabilities.Mixer);
    private bool CanMoveCrossfader() => IsActive && (CanWrite || sendingCrossfader) && Supports(PlaybackCapabilities.Mixer);
    private bool CanEqualize() => CanWrite && Supports(PlaybackCapabilities.Equalizer);

    private bool CanSeek(SeekGesture? input) => CanWrite && Supports(PlaybackCapabilities.Seek) && input is not null &&
        input.Position >= TimeSpan.Zero && (input.DeckId is { } deck
            ? State is { Decks.IsDefaultOrEmpty: false } current && current.Decks.Any(item => item.DeckId == deck && item.TrackId == input.TrackId)
            : State?.ActiveTrackId == input.TrackId);

    private bool Supports(PlaybackCapabilities capability) => (State?.Capabilities & capability) == capability;

    private Task TransportAsync(TransportAction action, DeckId? deck, CancellationToken token) =>
        Send(new Transport(playback.SessionId, action, deck), token);
    private Task MixAsync(MixEdit edit, CancellationToken token) => Send(new SetMix(playback.SessionId, edit), token);
    private Task EqAsync(EqualizerEdit edit, CancellationToken token) => Send(new SetEqualizer(playback.SessionId, edit), token);
    private Task Send(ICommand<PlaybackState> request, CancellationToken token) =>
        WriteAsync(request, playback.Apply, token, playback.Accepts);
}
