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

/// <summary>Verifies Linux playback shortcut routing over a private D-Bus instance.</summary>
/// <remarks>2026-11-25: Bind FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-002 to real portal message framing.</remarks>
[Trait("Platform", "Linux")]
public sealed class PortalShortcutIntegrationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-portal-shortcut-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    /// <summary>Uses the production Linux adapter with no bus address and resolves a local binding after failure.</summary>
    /// <remarks>2026-11-26: Verify missing native registration keeps in-app mappings usable for FR-SETTINGS-001#ac-shortcuts.</remarks>
    [Fact]
    public async Task MissingPortalKeepsInAppMappingsUsable()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var context = new CommandContext(request, Guid.NewGuid());
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => new RecordingOutput(),
            openShortcutPlatform: () => new XdgShortcutPlatform(null));
        var workspace = Value(await desktop.StartAsync(token));
        var routes = Assert.IsAssignableFrom<IRequestDispatcher>(workspace.Workspace);
        var original = Value(await routes.QueryAsync(new GetShortcutBindings(ShortcutScopes.Player), request, token));
        var unavailable = Assert.IsType<Outcome<ShortcutSnapshot>.DependencyFailure>(await routes.SendAsync(
            new SetGlobalPlaybackShortcuts(true, [], original.SystemRevision), context, token));
        Assert.Equal("system-playback-shortcuts-unavailable", unavailable.Code);
        var subsequent = Value(await routes.QueryAsync(new GetShortcutBindings(ShortcutScopes.Player), request, token));
        Assert.False(subsequent.GlobalPlaybackKeysEnabled);
        Assert.Equal(original.Revision, subsequent.Revision);
        Assert.Equal(original.SystemRevision, subsequent.SystemRevision);
        Assert.Equal(original.Bindings.AsEnumerable(), subsequent.Bindings.AsEnumerable());
        var resolved = Value(await routes.QueryAsync(new ResolveContextualShortcut([ShortcutScopes.Player], "Space"), request, token));
        Assert.Equal(ShortcutCatalog.PlayPause, resolved.Binding?.ActionId);
    }

    /// <summary>Verifies registration and activation on the same connection for both generations of the portal.</summary>
    /// <remarks>2026-11-25: Check the host identity handshake for FR-SETTINGS-001#ac-shortcuts.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortalSignalsTogglePlaybackAndRevocationRetiresTheBinding(bool requireHostIdentity)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Runs dbus-daemon on Linux.");
        await using var portal = await PortalShortcutServiceFixture.OpenAsync(token, requireHostIdentity);
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var audio = new RecordingOutput();
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => audio,
            presentationScheduler: new InlineScheduler(), openShortcutPlatform: () => new XdgShortcutPlatform(portal.Address));
        var workspace = Value(await desktop.StartAsync(token));
        var route = Assert.IsAssignableFrom<IRequestDispatcher>(workspace.Workspace);
        var session = Assert.IsType<Guid>(workspace.PlaybackSessionId);
        var editor = Assert.IsType<DesktopPresentation>(workspace.Presentation).Shortcuts;
        Assert.Empty(portal.Bound);
        Value(await Send(new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.PlayNow)));
        var allowed = Value(await Send(new SetGlobalPlaybackShortcuts(true, [], 0)));
        Assert.True(allowed.GlobalPlaybackKeysEnabled);
        await editor.OpenCaptureCommand.ExecuteAsync(ShortcutScopes.Player);
        var original = Assert.Single(portal.Bound);
        Assert.Equal("", original.Parent);
        if (requireHostIdentity)
        {
            var registration = Assert.IsType<PortalShortcutServiceFixture.HostRegistration>(original.Registration);
            PortalShortcutAssertions.VerifyHostIdentity(registration.ApplicationId, registration.Peer, original.Owner, registration.Order < original.CreationOrder);
        }
        else
        {
            Assert.Null(original.Registration);
        }

        var denied = Assert.IsType<Outcome<ShortcutSnapshot>.DependencyFailure>(await Send(new SetGlobalPlaybackShortcuts(true,
            [new(ShortcutCatalog.Stop, ShortcutScopes.System, "MediaStop")], allowed.SystemRevision)));
        var rejected = Assert.Single(portal.Bound, item => item.Path != original.Path);
        await UntilAsync(() => portal.Closed.Contains(rejected));
        var priorRetained = !portal.Closed.Contains(original);
        Assert.True((await Shortcuts()).GlobalPlaybackKeysEnabled);
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var pending = route.SendAsync(new SetGlobalPlaybackShortcuts(true,
                [new(ShortcutCatalog.SkipNext, ShortcutScopes.System, "MediaNextTrack")], allowed.SystemRevision),
                new(context, Guid.NewGuid()), cancellation.Token);
            await UntilAsync(() => !portal.HeldRequests.IsEmpty);
            await cancellation.CancelAsync();
            Assert.IsType<Outcome<ShortcutSnapshot>.Cancelled>(await pending);
            var handle = Assert.Single(portal.HeldRequests);
            await UntilAsync(() => portal.ClosedRequests.Contains(handle));
            Assert.Equal(allowed.SystemRevision, (await Shortcuts()).SystemRevision);
            Assert.DoesNotContain(original, portal.Closed);
        }
        portal.ChangeTrigger(original, "Super+P");
        await UntilAsync(() => editor.State?.RegisteredSystemBindings.SingleOrDefault()?.SystemTriggerDescription == "Super+P");
        Assert.Equal(ShortcutScopes.Player, editor.State?.Scope);
        portal.Activate(original, "library.search");
        portal.Activate(original, ShortcutCatalog.PlayPause);
        await UntilAsync(() => audio.Transports.Contains(TransportStatus.Paused));
        portal.Revoke(original);
        await UntilConditionAsync(async () => !(await Shortcuts()).GlobalPlaybackKeysEnabled);
        await UntilAsync(() => editor.State?.GlobalPlaybackKeysEnabled == false);
        var removed = await Shortcuts();
        Assert.Equal(allowed.SystemRevision + 2, removed.SystemRevision);
        Assert.Equal(allowed.Revision, removed.Revision);
        portal.Activate(original, ShortcutCatalog.PlayPause);
        await Task.Delay(100, token);
        Value(await desktop.StopAsync(token));
        PortalShortcutAssertions.Verify(new(original.PreferredTrigger, Assert.Single(allowed.RegisteredSystemBindings).SystemTriggerDescription,
            denied.Code, priorRetained, portal.Closed.Contains(rejected), audio.Transports.Select(status => status.ToString()).ToArray(),
            removed.GlobalPlaybackKeysEnabled));

        Task<Outcome<T>> Send<T>(ICommand<T> request) => route.SendAsync(request, new(context, Guid.NewGuid()), token);
        async Task<ShortcutSnapshot> Shortcuts() => Value(await route.QueryAsync(new GetShortcutBindings(ShortcutScopes.All), context, token));
        Task UntilAsync(Func<bool> check) => UntilConditionAsync(() => Task.FromResult(check()));
        async Task UntilConditionAsync(Func<Task<bool>> check)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            while (!await check())
                await Task.Delay(15, limit.Token);
        }
    }

    private static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    public void Dispose() => root.Delete(recursive: true);

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutput : IDesktopPlaybackOutput
    {
        private DeckState? current;
        internal ConcurrentQueue<TransportStatus> Transports { get; } = new();

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? start, TransportStatus status, CancellationToken token)
        {
            current = new(deck, trackId, start ?? current?.Position ?? TimeSpan.Zero, TimeSpan.FromSeconds(20), status, true, 1);
            Transports.Enqueue(status);
            return ReadAsync(deck, trackId, token);
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) =>
            Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(current!));

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
            ChangeAsync(deck, trackId, position, current!.Status, token);

        public void Dispose() { }
    }
}
