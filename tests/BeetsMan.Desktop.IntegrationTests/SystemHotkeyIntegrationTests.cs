using System.Collections.Concurrent;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs native ownership and activation through the composed desktop and the Win32 adapter's API seam.</summary>
/// <remarks>2026-11-25: Bind FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002 without using a physical keyboard.</remarks>
public sealed class SystemHotkeyIntegrationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-system-hotkey-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task NativeReplacementRollsBackAndAcceptedEventsToggleTheAuthoritativeSession()
    {
        var api = new Win32Messages();
        var output = new PlaybackOutput();
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => output,
            presentationScheduler: new InlineScheduler(), openShortcutPlatform: () => new Win32ShortcutPlatform(api));
        var workspace = Success(await desktop.StartAsync(token));
        var dispatcher = Assert.IsAssignableFrom<IRequestDispatcher>(workspace.Workspace);
        var session = Assert.IsType<Guid>(workspace.PlaybackSessionId);
        var presentation = Assert.IsType<DesktopPresentation>(workspace.Presentation);
        Assert.Empty(api.Active);
        var track = Guid.NewGuid();
        Success(await Send(new EnqueueTracks(session, [track], EnqueueMode.PlayNow)));
        var initial = Success(await Send(new SetGlobalPlaybackShortcuts(true, [], 0)));
        var registered = Assert.Single(api.Active);
        var attempted = await Send(new SetGlobalPlaybackShortcuts(true, [
            new(ShortcutCatalog.SkipNext, ShortcutScopes.System, "MediaNextTrack"),
            new(ShortcutCatalog.PlayPause, ShortcutScopes.System, "MediaPlayPause"),
            new(ShortcutCatalog.Stop, ShortcutScopes.System, "MediaStop")
        ], initial.SystemRevision));
        var denied = Assert.IsType<Outcome<ShortcutSnapshot>.DependencyFailure>(attempted);
        Assert.Equal("system-playback-shortcut-denied", denied.Code);
        var read = Success(await dispatcher.QueryAsync(new GetShortcutBindings(ShortcutScopes.All), context, token));
        Assert.Equal(initial.SystemRevision, read.SystemRevision);
        Assert.Single(read.RegisteredSystemBindings);
        var priorRetained = api.Active.TryGetValue(registered.Key, out _);
        var temporaryReleased = api.Registered.Any(item => item.Key.Key == 0xB0 && api.Released.Contains(item.Id));

        api.Activate(registered.Key);
        api.Activate(registered.Key);
        await WaitAsync(() => output.States.Count >= 3 && presentation.Playback.State?.Status == TransportStatus.Playing);
        var dispatched = output.States.Select(status => status.ToString()).ToArray();
        var after = Success(await dispatcher.QueryAsync(new GetPlaybackState(session), context, token));
        Assert.Equal(track, after.ActiveTrackId);
        var disabled = Success(await Send(new SetGlobalPlaybackShortcuts(false, [], initial.SystemRevision)));
        Assert.False(disabled.GlobalPlaybackKeysEnabled);
        Assert.Empty(api.Active);
        var again = Success(await Send(new SetGlobalPlaybackShortcuts(true, [], disabled.SystemRevision)));
        Assert.True(again.GlobalPlaybackKeysEnabled);
        var replacement = Assert.Single(api.Active);
        Assert.NotEqual(registered.Key, replacement.Key);
        api.Activate(registered.Key);
        api.Activate(replacement.Key);
        await WaitAsync(() => output.States.Count >= 4 && presentation.Playback.State?.Status == TransportStatus.Paused);
        Assert.Equal(4, output.States.Count);
        Success(await desktop.StopAsync(token));
        SystemHotkeyAssertions.Verify(new(initial.GlobalPlaybackKeysEnabled, denied.Code == "system-playback-shortcut-denied",
            registered.Value.Key, registered.Value.Modifiers, priorRetained, temporaryReleased, dispatched, api.Active.Count));
        Assert.Single(api.Threads.Distinct());

        Task<Outcome<TResult>> Send<TResult>(ICommand<TResult> value) => dispatcher.SendAsync(value, new(context, Guid.NewGuid()), token);
        async Task WaitAsync(Func<bool> ready)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(4));
            while (!ready())
                await Task.Delay(15, limit.Token);
        }
    }

    private static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    public void Dispose() => root.Delete(recursive: true);

    private sealed class Win32Messages : IWin32ShortcutApi
    {
        private readonly ConcurrentQueue<int> events = new();
        internal ConcurrentDictionary<int, NativeHotkey> Active { get; } = new();
        internal ConcurrentQueue<(int Id, NativeHotkey Key)> Registered { get; } = new();
        internal ConcurrentQueue<int> Released { get; } = new();
        internal ConcurrentQueue<int> Threads { get; } = new();

        public bool Register(int id, NativeHotkey key)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (key.Key == 0xB2)
                return false;
            Registered.Enqueue((id, key));
            return Active.TryAdd(id, key);
        }

        public void Unregister(int id)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (Active.TryRemove(id, out _))
                Released.Enqueue(id);
        }

        public bool TryReadActivation(out int id) => events.TryDequeue(out id);
        internal void Activate(int id) => events.Enqueue(id);
    }

    private sealed class PlaybackOutput : IDesktopPlaybackOutput
    {
        private DeckState? deck;
        internal ConcurrentQueue<TransportStatus> States { get; } = new();

        public Task<Outcome<DeckState>> ChangeAsync(DeckId id, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            deck = new(id, track, position ?? deck?.Position ?? TimeSpan.Zero, TimeSpan.FromSeconds(30), status, true, 1);
            States.Enqueue(status);
            return ReadAsync(id, track, token);
        }
        public Task<Outcome<DeckState>> ReadAsync(DeckId id, Guid track, CancellationToken token) =>
            Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(deck!));
        public Task<Outcome<DeckState>> SeekAsync(DeckId id, Guid track, TimeSpan position, CancellationToken token) =>
            ChangeAsync(id, track, position, deck!.Status, token);
        public void Dispose() { }
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
