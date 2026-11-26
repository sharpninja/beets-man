using System.Net;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Exercises the automatic Android feed, existing ViewModels, and startup and shutdown through the real local HTTPS host.</summary>
    /// <remarks>2026-11-25: Verify ownership, silent bootstrap, active playlist draft and playback continuity for FR-FILESYNC-001#ac-arrival.</remarks>
    /// <remarks>2026-11-26: Verify arrival from every presentation mode for TEST-FILESYNC-002 and FR-FILESYNC-001#ac-arrival.</remarks>
    /// <param name="initialMode">The presentation mode active when the registered Android peer reconnects.</param>
    [Theory]
    [InlineData(PresentationMode.Normal)]
    [InlineData(PresentationMode.Minimum)]
    [InlineData(PresentationMode.BoundedVisualization)]
    [InlineData(PresentationMode.FullVisualization)]
    public async Task ComposedDesktopStartsAndStopsTheAuthenticatedAndroidArrivalMonitor(PresentationMode initialMode)
    {
        var hostDatabase = Path.Combine(root, "automatic-arrival-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var export = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
        var native = new AndroidNativeEventFixture();
        await using var nativeApp = native.Build();
        await nativeApp.StartAsync(Token);
        var nativeBase = new Uri(nativeApp.Urls.Single() + '/');
        var phone = new RegisteredAndroidExport(library, Guid.NewGuid(), 1, "android", "phone", new(nativeBase, "phone/"), false);
        SeedHost(hostDatabase, [phone]);
        var options = new LibraryHostOptions(library, hostDatabase, music, Path.Combine(root, "private"), Path.Combine(root, "work"),
            [new(phone.DeviceId, phone.ShareId, new(new(nativeBase, "sender/"), StartupSyncthingApi.SenderId, export))]);
        var identity = new Verifier(new(library, instance), secret, null);
        var credentials = new StartupSyncthingCredentials(library, phone.DeviceId, Guid.Empty);
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        await using var host = LibraryHostStartup.Build(builder, options, identity, credentials, credentials);
        await host.StartAsync(Token);
        await native.WaitUntilAsync(() => native.RequestedAfter == 1, Token);

        var address = new Uri(host.Urls.Single());
        var observations = new ArrivalFeedObserver(library, CreateClientHandler(certificate));
        using var client = new HttpClient(observations);
        var audio = new ArrivalPlaybackOutput();
        var defaults = JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "desktop"), music);
        await using var desktop = new DesktopStartup(defaults, [], library, instance, _ => audio,
            host: new(address, client, new TokenProvider(library, instance, secret)), presentationScheduler: new InlineScheduler());
        var started = Value(await desktop.StartAsync(Token));
        var presentation = Assert.IsType<DesktopPresentation>(started.Presentation);
        var session = Assert.IsType<Guid>(started.PlaybackSessionId);
        var dispatcher = Assert.IsAssignableFrom<IRequestDispatcher>(started.Workspace);
        var request = new RequestContext(Guid.NewGuid(), instance);
        var track = Guid.NewGuid();
        var draft = await SendAsync(new CreatePlaylistDraft(new PlaylistDraftSource.Empty()));
        draft = await SendAsync(new EditPlaylistDraft(draft.DraftId, new PlaylistDraftEdit.Append([track]), draft.Revision));
        await SendAsync(new EnqueueTracks(session, [track], EnqueueMode.PlayNow));
        var playing = await SendAsync(new Seek(session, track, TimeSpan.FromSeconds(102)));
        await presentation.Shell.ChangePresentationCommand.ExecuteAsync(new ModeSelection(initialMode));
        await presentation.Shell.OpenPresentationChooserCommand.ExecuteAsync(null);
        var beforeArrival = Assert.IsType<ShellState>(presentation.Shell.State);
        Assert.Equal(initialMode, beforeArrival.Mode);
        var overlay = Assert.Single(beforeArrival.Overlays);
        await native.WaitUntilAsync(() => observations.CursorRequests > 0, Token);
        var baselineSilent = presentation.Shell.State!.Route.Workspace == Workspace.Library && presentation.Sync.SelectedPeerId is null;
        var again = Value(await desktop.StartAsync(Token));
        var reused = ReferenceEquals(again.Presentation, presentation) && ReferenceEquals(again.Workspace, started.Workspace);

        native.Add(2, "phone", false);
        await native.WaitUntilAsync(() => native.RequestedAfter == 2, Token);
        native.Add(3, "phone", true);
        await native.WaitUntilAsync(() => presentation.Sync.SelectedPeerId == phone.DeviceId, Token);
        var shell = presentation.Shell.State!;
        var selected = shell.Route == new WorkspaceRoute(Workspace.Sync, phone.DeviceId) && shell.Mode == PresentationMode.Normal &&
            presentation.Sync.State!.Peers.Single(peer => peer.PeerId == phone.DeviceId).Connected;
        var retained = Value(await dispatcher.QueryAsync(new GetPlaylistDraft(draft.DraftId), request, Token));
        var draftPreserved = shell.Overlays.SequenceEqual([overlay]) && retained.Revision == draft.Revision && retained.Entries.SequenceEqual(draft.Entries);
        var after = Value(await dispatcher.QueryAsync(new GetPlaybackState(session), request, Token));
        var playbackPreserved = shell.PlaybackSessionId == session && after.Status == TransportStatus.Playing &&
            after.ActiveQueueEntryId == playing.ActiveQueueEntryId && after.Position == playing.Position && audio.Starts == 1;

        await native.WaitUntilAsync(() => observations.Active == 1, Token);
        var stopped = Value(await desktop.StopAsync(Token));
        var cancelled = observations.Cancelled > 0 && observations.Active == 0 && stopped?.ActiveQueueEntryId == playing.ActiveQueueEntryId;
        var deactivated = !presentation.Shell.IsActive && !presentation.Sync.IsActive;
        StageAssertions.VerifyComposedAndroidArrival(new(baselineSilent, reused, observations.Maximum, selected,
            draftPreserved, playbackPreserved, cancelled, deactivated));

        async Task<T> SendAsync<T>(ICommand<T> command) => Value(await dispatcher.SendAsync(command, new(request, Guid.NewGuid()), Token));
        static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class ArrivalFeedObserver(Guid libraryId, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly object gate = new();
        private int active;
        private int maximum;
        private int cancelled;
        private int cursorRequests;

        internal int Active { get { lock (gate) return active; } }
        internal int Maximum { get { lock (gate) return maximum; } }
        internal int Cancelled { get { lock (gate) return cancelled; } }
        internal int CursorRequests { get { lock (gate) return cursorRequests; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.TrimStart('/') != AndroidArrivalRoute.ReadPath(libraryId).TrimStart('/'))
                return await base.SendAsync(request, token);

            lock (gate)
            {
                active++;
                maximum = Math.Max(active, maximum);
                if (uri.Query.Contains("after=", StringComparison.Ordinal))
                    cursorRequests++;
            }
            try { return await base.SendAsync(request, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (gate) cancelled++;
                throw;
            }
            finally { lock (gate) active--; }
        }
    }

    private sealed class ArrivalPlaybackOutput : IDesktopPlaybackOutput
    {
        private DeckState? loaded;
        internal int Starts { get; private set; }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            Starts++;
            loaded = new(deck, trackId, position ?? TimeSpan.Zero, TimeSpan.FromMinutes(5), status, true, 1);
            return Result();
        }

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token)
        {
            loaded = loaded! with { Position = position };
            return Result();
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) => Result();

        public void Dispose() => loaded = null;

        private Task<Outcome<DeckState>> Result() => Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(loaded!));
    }
}
