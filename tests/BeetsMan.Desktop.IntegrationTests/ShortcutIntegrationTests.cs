using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies shortcut requests through the desktop composition, Toolkit, and real LiteDB.</summary>
/// <remarks>2026-11-25: Bind FR-SETTINGS-001#ac-shortcuts and TEST-SETTINGS-001 to contextual and OS outcomes.</remarks>
public sealed partial class SETTINGSAcceptanceTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-shortcuts-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    /// <summary>Retains the original gesture on conflict and OS denial and saves a replacement across restart.</summary>
    /// <remarks>2026-11-26: Bind the named contextual shortcut case for FR-SETTINGS-001#ac-shortcuts.</remarks>
    [Fact]
    public async Task Shortcuts_MatchesContract()
    {
        var origin = Guid.NewGuid();
        var library = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var platform = new TestPlatform();
        string winner, conflictCode, modalAction, assignedGesture, denialCode;
        long revisionBefore, revisionAfter;
        await using (var desktop = Open(() => platform))
        {
            var workspace = Success(await desktop.StartAsync(token));
            var bus = Assert.IsAssignableFrom<IRequestDispatcher>(workspace.Workspace);
            var model = Assert.IsType<DesktopPresentation>(workspace.Presentation).Shortcuts;
            await model.OpenCaptureCommand.ExecuteAsync(ShortcutScopes.Player);
            var initial = Assert.IsType<ShortcutSnapshot>(model.State);
            await model.AssignCapturedCommand.ExecuteAsync(new ShortcutAssignment(
                new(ShortcutCatalog.ToggleAutoFade, ShortcutScopes.Player, "space"), initial.Revision));
            conflictCode = Assert.Single(model.Failure!.Issues).Code;
            winner = Assert.IsType<ShortcutBinding>(model.WinningBinding).ActionId;
            Assert.NotNull(model.PendingAssignment);
            Assert.Equal(initial.Revision, model.State!.Revision);
            Assert.Equal("Space", Assert.Single(model.State.Bindings).Gesture);

            await model.ChooseAnotherCommand.ExecuteAsync(ShortcutScopes.Player);
            Assert.Null(model.PendingAssignment);
            Assert.Null(model.WinningBinding);
            await model.AssignCapturedCommand.ExecuteAsync(new ShortcutAssignment(
                new(ShortcutCatalog.ToggleAutoFade, ShortcutScopes.Player, "Shift+Control+f"), initial.Revision));
            var updated = Assert.IsType<ShortcutSnapshot>(model.State);
            assignedGesture = Gesture(updated, ShortcutCatalog.ToggleAutoFade);
            Assert.Null(model.PendingAssignment);
            Assert.Equal("stale-shortcut-bindings", Assert.IsType<Outcome<ShortcutSnapshot>.Conflict>(
                await Send(new AssignShortcut(ShortcutCatalog.ToggleAutoFade, ShortcutScopes.Player, "Ctrl+G", initial.Revision))).Code);

            var libraryEdit = Success(await Send(new AssignShortcut(ShortcutCatalog.SearchLibrary, ShortcutScopes.Library, "Escape", updated.Revision)));
            var contextual = Success(await bus.QueryAsync(new ResolveContextualShortcut([ShortcutScopes.Player, ShortcutScopes.Library, ShortcutScopes.Dialog], "Esc"),
                request, token));
            modalAction = Assert.IsType<ShortcutBinding>(contextual.Binding).ActionId;
            var libraryOnly = Success(await bus.QueryAsync(new ResolveContextualShortcut([ShortcutScopes.Library], "Esc"), request, token));
            Assert.Equal(ShortcutCatalog.SearchLibrary, libraryOnly.Binding?.ActionId);
            Assert.Equal("invalid-system-playback-shortcuts", Assert.Single(Assert.IsType<Outcome<ShortcutSnapshot>.Invalid>(
                await Send(new SetGlobalPlaybackShortcuts(true, [new(ShortcutCatalog.SearchLibrary, ShortcutScopes.System, "Ctrl+F")], 0))).Issues).Code);
            Assert.Equal(0, platform.Calls);
            await model.OpenCaptureCommand.ExecuteAsync(ShortcutScopes.Player);
            var before = Assert.IsType<ShortcutSnapshot>(model.State);
            revisionBefore = before.Revision;
            Assert.Equal(libraryEdit.Revision, revisionBefore);
            var registering = model.SetSystemPlaybackKeysCommand.ExecuteAsync(new SystemShortcutSelection(true, [], before.SystemRevision));
            await platform.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.True(model.IsInteractionBlocked);
            Assert.True(model.RequestedSystemPlaybackKeys);
            var whileRegistering = Success(await bus.QueryAsync(new GetShortcutBindings(ShortcutScopes.Player), request, token));
            Assert.Equal("Space", Gesture(whileRegistering, ShortcutCatalog.PlayPause));
            platform.Release.TrySetResult(false);
            await registering.WaitAsync(TimeSpan.FromSeconds(2), token);
            Assert.False(model.IsInteractionBlocked);
            denialCode = Assert.IsType<PresentationFailure>(model.Failure).Code!;
            revisionAfter = Assert.IsType<ShortcutSnapshot>(model.State).Revision;
            Assert.False(model.State!.GlobalPlaybackKeysEnabled);
            Assert.Equal(0, model.State.SystemRevision);
            Assert.Equal("MediaPlayPause", Assert.Single(platform.Bindings).Gesture);
            Success(await desktop.StopAsync(token));
            Assert.True(platform.Disposed);

            Task<Outcome<T>> Send<T>(ICommand<T> command) => bus.SendAsync(command, new(request, Guid.NewGuid()), token);
        }
        using (var database = new LiteDatabase(settings.DatabasePath))
        {
            var document = Assert.Single(database.GetCollection<BsonDocument>("shortcut_bindings").FindAll());
            Assert.Equal(2, document["Overrides"].AsArray.Count);
            Assert.Equal(revisionAfter, document["Revision"].AsInt64);
            Assert.False(document.ContainsKey("SystemRevision"));
        }
        await using var reopened = Open();
        var current = Success(await reopened.StartAsync(token));
        var models = Assert.IsType<DesktopPresentation>(current.Presentation);
        await models.Shortcuts.OpenCaptureCommand.ExecuteAsync(ShortcutScopes.Player);
        var restored = Assert.IsType<ShortcutSnapshot>(models.Shortcuts.State);
        ShortcutAssertions.Verify(new(conflictCode, winner, modalAction, assignedGesture, Gesture(restored, ShortcutCatalog.ToggleAutoFade),
            restored.GlobalPlaybackKeysEnabled, denialCode, revisionBefore, revisionAfter, Gesture(restored, ShortcutCatalog.PlayPause)));
        await models.Shortcuts.ResetBindingsCommand.ExecuteAsync(null);
        var reset = Assert.IsType<ShortcutSnapshot>(models.Shortcuts.State);
        Assert.Equal("Space", Assert.Single(reset.Bindings).Gesture);
        await models.Shortcuts.OpenCaptureCommand.ExecuteAsync(ShortcutScopes.Library);
        Assert.Equal("Esc", Gesture(models.Shortcuts.State!, ShortcutCatalog.SearchLibrary));

        DesktopStartup Open(Func<ISystemPlaybackShortcutPlatform>? factory = null) => new(settings, [], library, origin, _ => new UnusedOutput(),
            presentationScheduler: new InlineScheduler(), openShortcutPlatform: factory);
    }

    /// <summary>Applies, versions and releases system registrations independently of stored in-app gestures.</summary>
    [Fact]
    public async Task SystemSuccessAndDisableUseASeparateRevisionFromLocalBindings()
    {
        var request = new RequestContext(Guid.NewGuid(), Guid.NewGuid());
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var platform = new TestPlatform();
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), request.OriginInstanceId!.Value,
            _ => new UnusedOutput(), openShortcutPlatform: () => platform);
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(Success(await desktop.StartAsync(token)).Workspace);
        var applying = Send(new SetGlobalPlaybackShortcuts(true, [], 0));
        await platform.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
        var changed = Success(await bus.SendAsync(new AssignShortcut(ShortcutCatalog.ToggleAutoFade, ShortcutScopes.Player,
            "Ctrl+Shift+F", 0), new(request, Guid.NewGuid()), token));
        platform.Release.SetResult(true);
        var registered = Success(await applying);
        Assert.True(registered.GlobalPlaybackKeysEnabled);
        Assert.Equal(1, registered.SystemRevision);
        Assert.Equal(changed.Revision, registered.Revision);
        Assert.Equal("Ctrl+Shift+F", Gesture(registered, ShortcutCatalog.ToggleAutoFade));
        Assert.Equal(ShortcutCatalog.DefaultSystemBindings, registered.RegisteredSystemBindings);
        Assert.Equal("stale-system-shortcuts", Assert.IsType<Outcome<ShortcutSnapshot>.Conflict>(
            await Send(new SetGlobalPlaybackShortcuts(false, [], 0))).Code);
        var same = Success(await Send(new SetGlobalPlaybackShortcuts(true, [], 1)));
        Assert.Equal(registered.SystemRevision, same.SystemRevision);
        Assert.Equal(1, platform.Calls);
        var cleared = Success(await Send(new SetGlobalPlaybackShortcuts(false, [], 1)));
        Assert.False(cleared.GlobalPlaybackKeysEnabled);
        Assert.Equal(2, cleared.SystemRevision);
        Assert.Equal(changed.Revision, cleared.Revision);
        Assert.Empty(platform.Bindings);
        Assert.Equal(2, platform.Calls);

        Task<Outcome<ShortcutSnapshot>> Send(SetGlobalPlaybackShortcuts command) =>
            bus.SendAsync(command, new(request, Guid.NewGuid()), token);
    }

    private static string Gesture(ShortcutSnapshot state, string action) => Assert.Single(state.Bindings, binding => binding.ActionId == action).Gesture;
    private static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    public void Dispose() => root.Delete(recursive: true);

    private sealed class TestPlatform : ISystemPlaybackShortcutPlatform
    {
        public event Action<ShortcutBinding>? Activated { add { } remove { } }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ImmutableArray<ShortcutBinding> Bindings { get; private set; }
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }

        public async Task<Outcome<bool>> ApplyAsync(ImmutableArray<ShortcutBinding> bindings, CancellationToken token)
        {
            Bindings = bindings;
            Calls++;
            Started.TrySetResult();
            return new Outcome<bool>.Success(await Release.Task.WaitAsync(token));
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedOutput : IDesktopPlaybackOutput
    {
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token) =>
            throw new InvalidOperationException("Shortcut editing does not use audio output.");
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) =>
            throw new InvalidOperationException("Shortcut editing does not use audio output.");
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
            throw new InvalidOperationException("Shortcut editing does not use audio output.");
        public void Dispose() { }
    }
}
