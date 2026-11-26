using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies that an unprojected live connection keeps its original generation until the desktop handles it.</summary>
/// <remarks>2026-11-26: Bind retry and exactly one navigation to FR-FILESYNC-001#ac-arrival.</remarks>
public sealed class AndroidArrivalMonitorTests
{
    /// <summary>Retries a typed shell failure or cancellation before acknowledging the host revision.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TransientCqrsFailureRetainsFreshArrivalForRetry(bool cancellation) => VerifyRetryAsync(shellFails: true, cancellation);

    /// <summary>Retries loading the destination peer without submitting navigation again.</summary>
    [Fact]
    public Task MediaProjectionFailureRetriesWithoutDuplicateNavigation() => VerifyRetryAsync(shellFails: false, cancellation: false);

    private static async Task VerifyRetryAsync(bool shellFails, bool cancellation)
    {
        var token = TestContext.Current.CancellationToken;
        var origin = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var window = Guid.NewGuid();
        var host = new RepeatingArrivalHost(peer);
        using var monitor = new AndroidArrivalMonitor(host, origin);
        var initial = new ShellState(window, 1, new(Workspace.Library), PresentationMode.FullVisualization,
            PresentationMode.BoundedVisualization, Guid.NewGuid(), [], null);
        var commands = new RetryOnceShell(new ShellHandlers(initial, monitor), shellFails, cancellation);
        var mediaQuery = new RetryOnceMedia(peer, !shellFails);
        var dispatcher = new RequestDispatcher.Builder().RegisterCommand(commands).RegisterQuery(mediaQuery).Build();
        var services = new PresentationServices(dispatcher, new ArrivalScheduler(), origin);
        using var shell = new ShellViewModel(services, window, initial);
        using var media = new MediaSyncViewModel(services, Guid.NewGuid(), shell);
        shell.Activate(window);
        media.Activate(media.Scope.OwnerId);

        var baseline = await monitor.PollOnceAsync(shell, media, token);
        Assert.True(Assert.IsType<Outcome<AndroidArrivalFrame>.Success>(baseline).Value.Baseline);
        Assert.Empty(commands.Generations);
        Assert.Equal(0, mediaQuery.Calls);

        var failed = await monitor.PollOnceAsync(shell, media, token);
        var retryable = failed is Outcome<AndroidArrivalFrame>.DependencyFailure { Retryable: true };
        var firstRoute = shell.State!.Route.Workspace;
        var unselectedAfterFailure = media.SelectedPeerId is null;
        var retried = await monitor.PollOnceAsync(shell, media, token);
        var duplicate = await monitor.PollOnceAsync(shell, media, token);
        var observed = new AndroidArrivalRetryObservation(retryable, firstRoute, unselectedAfterFailure,
            retried is Outcome<AndroidArrivalFrame>.Success, duplicate is Outcome<AndroidArrivalFrame>.Success,
            [.. host.Cursors], [.. commands.Generations], commands.NavigationCount, mediaQuery.Calls, shell.State.Route,
            shell.State.Mode, media.SelectedPeerId);
        AndroidArrivalRetryAssertions.Verify(observed, shellFails, peer);
    }

    /// <summary>Returns the same current host state for both retries and duplicate reads.</summary>
    private sealed class RepeatingArrivalHost(Guid peer) : IAndroidArrivalHost
    {
        private readonly Guid epoch = Guid.NewGuid();
        public List<long?> Cursors { get; } = [];

        public Task<Outcome<AndroidArrivalFrame>> ReadArrivalsAsync(Guid? requestedEpoch, long? after, RequestContext context,
            CancellationToken token)
        {
            Cursors.Add(after);
            Assert.Equal(after is null ? null : epoch, requestedEpoch);
            var frame = after is null ? new AndroidArrivalFrame(epoch, 1, true, [new(peer, false, null)]) :
                new AndroidArrivalFrame(epoch, 2, false, [new(peer, true, 2)]);
            return Task.FromResult<Outcome<AndroidArrivalFrame>>(new Outcome<AndroidArrivalFrame>.Success(frame));
        }
    }

    /// <summary>Can fail the first navigation and tracks accepted navigation through the real shell command handler.</summary>
    private sealed class RetryOnceShell(ShellHandlers handler, bool fails, bool cancellation) : ICommandHandler<ApplyAndroidPeerArrival, ShellState>
    {
        public List<long> Generations { get; } = [];
        public int NavigationCount { get; private set; }

        public async Task<Outcome<ShellState>> HandleAsync(ApplyAndroidPeerArrival request, CommandContext context, CancellationToken token)
        {
            Generations.Add(request.ConnectionGeneration);
            if (fails && Generations.Count == 1)
                return cancellation ? new Outcome<ShellState>.Cancelled() : new Outcome<ShellState>.DependencyFailure("test-shell-unavailable", true);
            var result = await handler.HandleAsync(request, context, token);
            if (result is Outcome<ShellState>.Success { Value.Route.Workspace: Workspace.Sync })
                NavigationCount++;
            return result;
        }
    }

    /// <summary>Can fail the first media read, then supplies the registered Android peer.</summary>
    private sealed class RetryOnceMedia(Guid peer, bool fails) : IQueryHandler<GetMediaSyncState, MediaSyncState>
    {
        public int Calls { get; private set; }

        public Task<Outcome<MediaSyncState>> HandleAsync(GetMediaSyncState request, RequestContext context, CancellationToken token)
        {
            Calls++;
            Outcome<MediaSyncState> result = fails && Calls == 1 ? new Outcome<MediaSyncState>.DependencyFailure("test-media-unavailable", true) :
                new Outcome<MediaSyncState>.Success(new(2, [new(peer, "Phone", MediaPeerKind.Android, true, 1)], []));
            return Task.FromResult(result);
        }
    }

    /// <summary>Executes presentation callbacks without a GUI host.</summary>
    private sealed class ArrivalScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}

/// <summary>Proves the arrival assertions with an independent success fixture and deliberately wrong observations.</summary>
/// <remarks>2026-11-26: Retain the green mock gate and expected negative controls for FR-FILESYNC-001#ac-arrival.</remarks>
public sealed class AndroidArrivalRetryMockTests
{
    /// <summary>Rejects cursor advancement, generation reuse errors, duplicate navigation, and a missing selected peer.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsLostArrivalAndRepeatedNavigation(bool shellFails)
    {
        var peer = Guid.Parse("acd1b897-a43e-4419-ae7d-47af42f65c33");
        var good = new AndroidArrivalRetryObservation(true, shellFails ? Workspace.Library : Workspace.Sync, true,
            true, true, [null, 1, 1, 2], shellFails ? [1L, 1L] : [1L], 1, shellFails ? 1 : 2,
            new(Workspace.Sync, peer), PresentationMode.Normal, peer);
        AndroidArrivalRetryAssertions.Verify(good, shellFails, peer);
        Reject(good with { Retryable = false });
        Reject(good with { FirstRoute = Workspace.Diagnostics });
        Reject(good with { UnselectedAfterFailure = false });
        Reject(good with { Retried = false });
        Reject(good with { DuplicateRead = false });
        Reject(good with { Cursors = [null, 1, 2, 2] });
        Reject(good with { Generations = [1, 2] });
        Reject(good with { NavigationCount = 2 });
        Reject(good with { MediaQueries = 3 });
        Reject(good with { FinalRoute = new(Workspace.Sync, Guid.Empty) });
        Reject(good with { Mode = PresentationMode.FullVisualization });
        Reject(good with { SelectedPeer = null });

        void Reject(AndroidArrivalRetryObservation bad) =>
            Assert.ThrowsAny<XunitException>(() => AndroidArrivalRetryAssertions.Verify(bad, shellFails, peer));
    }
}

/// <summary>Records which host frames, application operations, and presentation states were consumed.</summary>
/// <remarks>2026-11-26: Share mock and implementation expectations for FR-FILESYNC-001#ac-arrival.</remarks>
internal sealed record AndroidArrivalRetryObservation(bool Retryable, Workspace FirstRoute, bool UnselectedAfterFailure, bool Retried,
    bool DuplicateRead, long?[] Cursors, long[] Generations, int NavigationCount, int MediaQueries, WorkspaceRoute FinalRoute,
    PresentationMode Mode, Guid? SelectedPeer);

/// <summary>Checks that a failed step leaves an arrival retryable, then accepts its completed projection once.</summary>
/// <remarks>2026-11-26: Pin the arrival retry boundaries for FR-FILESYNC-001#ac-arrival.</remarks>
internal static class AndroidArrivalRetryAssertions
{
    internal static void Verify(AndroidArrivalRetryObservation result, bool shellFails, Guid peer)
    {
        Assert.True(result.Retryable);
        Assert.Equal(shellFails ? Workspace.Library : Workspace.Sync, result.FirstRoute);
        Assert.True(result.UnselectedAfterFailure);
        Assert.True(result.Retried);
        Assert.True(result.DuplicateRead);
        Assert.Equal([null, 1L, 1L, 2L], result.Cursors);
        Assert.Equal(shellFails ? [1L, 1L] : [1L], result.Generations);
        Assert.Equal(1, result.NavigationCount);
        Assert.Equal(shellFails ? 1 : 2, result.MediaQueries);
        Assert.Equal(new WorkspaceRoute(Workspace.Sync, peer), result.FinalRoute);
        Assert.Equal(PresentationMode.Normal, result.Mode);
        Assert.Equal(peer, result.SelectedPeer);
    }
}
