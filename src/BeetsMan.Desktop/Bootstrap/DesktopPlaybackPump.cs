using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Presentation;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Advances playback while the validated desktop workspace owns the audio device.</summary>
/// <remarks>2026-11-25: Poll outside the audio callback and stop before the final checkpoint for FR-PLAYER-001#ac-fade.</remarks>
internal sealed class DesktopPlaybackPump(IRequestDispatcher dispatcher, Guid session, Guid origin, PlaybackViewModel? player)
{
    private CancellationTokenSource? lifetime;
    private Task? polling;

    /// <summary>Starts or resumes the single non-overlapping playback loop.</summary>
    internal void Start()
    {
        if (lifetime is not null)
            return;
        lifetime = new CancellationTokenSource();
        polling = PollAsync(lifetime.Token);
    }

    /// <summary>Joins the loop so no transitions race a final playback checkpoint.</summary>
    internal async Task StopAsync()
    {
        if (lifetime is not { } run)
            return;
        try
        {
            await run.CancelAsync().ConfigureAwait(false);
            await polling!.ConfigureAwait(false);
        }
        finally
        {
            run.Dispose();
            lifetime = null;
            polling = null;
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var published = -1L;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var attempt = new CommandContext(new(Guid.NewGuid(), origin), Guid.NewGuid());
                var advanced = await dispatcher.SendAsync(new AdvancePlayback(session), attempt, token).ConfigureAwait(false);
                if (player is not null && advanced is Outcome<PlaybackState>.Success { Value: var next } && next.Revision != published)
                {
                    await player.ReceiveAsync(next).ConfigureAwait(false);
                    published = next.Revision;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
