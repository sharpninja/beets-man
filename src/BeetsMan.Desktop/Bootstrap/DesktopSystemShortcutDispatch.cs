using System.Threading.Channels;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Presentation;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Dispatches owned system playback key events through the active playback session.</summary>
/// <remarks>2026-11-25: Recheck registration and keep native event processing outside the playback lane for FR-SETTINGS-001#ac-shortcuts.</remarks>
internal sealed class DesktopSystemShortcutDispatch(ISystemPlaybackShortcutPlatform platform, ShortcutHandlers shortcuts,
    IRequestDispatcher dispatcher, Guid session, Guid origin, PlaybackViewModel? playback, ShortcutsViewModel? editor)
{
    private ActivationLane? active;

    internal void Start()
    {
        if (active is not null)
            return;
        var lane = new ActivationLane();
        Volatile.Write(ref active, lane);
        platform.Activated += OnActivated;
        platform.RegistrationChanged += OnRegistrationChanged;
        lane.Work = RunAsync(lane);
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref active, null) is not { } lane)
            return;
        platform.Activated -= OnActivated;
        platform.RegistrationChanged -= OnRegistrationChanged;
        lane.Events.Writer.TryComplete();
        await lane.Lifetime.CancelAsync().ConfigureAwait(false);
        try { await lane.Work.ConfigureAwait(false); }
        finally { lane.Lifetime.Dispose(); }
    }

    private void OnActivated(ShortcutBinding binding)
    {
        if (Volatile.Read(ref active) is { } lane && shortcuts.CaptureSystemActivation(binding) is { } revision)
            lane.Events.Writer.TryWrite((binding, revision));
    }

    private void OnRegistrationChanged() => Volatile.Read(ref active)?.Events.Writer.TryWrite((null, 0));

    private async Task RunAsync(ActivationLane lane)
    {
        var token = lane.Lifetime.Token;
        try
        {
            await foreach (var (binding, revision) in lane.Events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (binding is null)
                {
                    if (editor is not null && await dispatcher.QueryAsync(new GetShortcutBindings(ShortcutScopes.All),
                            new(Guid.NewGuid(), origin), token).ConfigureAwait(false) is Outcome<ShortcutSnapshot>.Success changed)
                        await editor.ReceiveSystemRegistrationAsync(changed.Value).ConfigureAwait(false);
                    continue;
                }
                if (shortcuts.CaptureSystemActivation(binding) != revision || Map(binding.ActionId) is not { } action)
                    continue;
                var result = await dispatcher.SendAsync(new Transport(session, action), new(new(Guid.NewGuid(), origin), Guid.NewGuid()),
                    token).ConfigureAwait(false);
                if (playback is not null && !token.IsCancellationRequested && result is Outcome<PlaybackState>.Success applied)
                    await playback.ReceiveAsync(applied.Value).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static TransportAction? Map(string action) => action switch
    {
        ShortcutCatalog.PlayPause => TransportAction.TogglePlayPause,
        ShortcutCatalog.SkipNext => TransportAction.Next,
        ShortcutCatalog.SkipPrevious => TransportAction.Previous,
        ShortcutCatalog.Stop => TransportAction.Stop,
        _ => null
    };

    private sealed class ActivationLane
    {
        internal Channel<(ShortcutBinding? Binding, long Revision)> Events { get; } =
            Channel.CreateBounded<(ShortcutBinding?, long)>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest });
        internal CancellationTokenSource Lifetime { get; } = new();
        internal Task Work { get; set; } = Task.CompletedTask;
    }
}
