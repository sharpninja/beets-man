using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Adapters.Processes;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using BeetsMan.Plugin.WorkerFixture;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs trusted fixture archives through the installed registry and the real child-process handshake.</summary>
/// <remarks>2026-11-25: Cover FR-PLUGINS-001#ac-visualize, ac-recovery and TEST-PLUGINS-002.</remarks>
public sealed class PluginWorkerIntegrationTests : IDisposable
{
    private const string Plugin = "example.visualizer";
    private const string Version = "1.0.0";
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-plugin-worker-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    /// <summary>Runs a real negotiated action through Toolkit, CQRS, and the selected playback backend.</summary>
    /// <remarks>2026-11-25: Bind FR-PLUGINS-001#ac-visualize and ac-recovery to exact grants and worker timeouts.</remarks>
    [Fact]
    public async Task PluginActionRequiresTheInstalledGrantAndReturnsTheAuthoritativePlayback()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var archive = CreateArchive("actions");
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => new TestPlaybackOutput(),
            presentationScheduler: new InlineScheduler());
        var active = Success(await desktop.StartAsync(token));
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
        var models = Assert.IsType<DesktopPresentation>(active.Presentation);
        var packages = Assert.IsType<LocalPluginPackages>(active.PluginPackages);
        var session = Assert.IsType<Guid>(active.PlaybackSessionId);
        var track = Guid.NewGuid();
        Success(await Submit(new InstallPluginPackage(packages.SelectPackage(archive))));
        var initial = await Submit(Action("pause", "literal note ; $(example)"));
        Assert.Equal("plugin-action-unavailable", Assert.Single(Assert.IsType<Outcome<PluginActionReceipt>.Invalid>(initial).Issues).Code);

        Success(await Submit(new SetPluginTrust(Plugin, Version, PluginTrust.Local)));
        Success(await Submit(new SetPluginEnabled(Plugin, Version, true)));
        await ObserveAsync(models.Extensions, PluginWorkerStatus.Ready);
        var playing = Success(await Submit(new EnqueueTracks(session, [track], EnqueueMode.PlayNow)));
        Assert.Equal("invalid-plugin-action", Issue(await Submit(Action("pause", "literal note ; $(example)") with { SessionId = Guid.NewGuid() })));
        Assert.Equal("invalid-plugin-action", Issue(await Submit(Action("pause", new string('x', 1025)))));
        Assert.Equal("plugin-host-action-denied", Issue(await Submit(Action("note", "literal note ; $(example)"))));
        Assert.Equal("plugin-host-action-denied", Issue(await Submit(Action("pause", "unauthorized"))));
        Assert.Equal(TransportStatus.Playing, Success(await bus.QueryAsync(new GetPlaybackState(session), request, token)).Status);

        var invocation = new ContributionInvocation(new(Plugin, Version), "pause", Arguments("literal note ; $(example)"));
        Assert.True(models.Extensions.RunContributionCommand.CanExecute(invocation));
        await models.Extensions.RunContributionCommand.ExecuteAsync(invocation);
        var receipt = Assert.IsType<PluginActionReceipt>(models.Extensions.LastAction);
        var paused = Assert.IsType<PlaybackState>(receipt.Playback);
        PluginActionAssertions.Verify(playing, paused, receipt.Message);
        Assert.Equal(paused, models.Playback.State);

        var ongoing = Submit(Action("pause", "timeout"));
        var responsive = Success(await bus.QueryAsync(new GetPlaybackState(session), request, token).WaitAsync(TimeSpan.FromSeconds(2), token));
        Assert.Equal(TransportStatus.Paused, responsive.Status);
        Assert.Equal("plugin-worker-timeout", Assert.IsType<Outcome<PluginActionReceipt>.DependencyFailure>(await ongoing).Code);
        var detached = await ObserveAsync(models.Extensions, PluginWorkerStatus.Failed);
        var worker = Assert.Single(detached.Plugins);
        Assert.Empty(worker.Contributions);
        Assert.Equal("plugin-worker-timeout", worker.FailureCode);
        Assert.False(models.Extensions.RunContributionCommand.CanExecute(invocation));

        Task<Outcome<T>> Submit<T>(ICommand<T> command) => bus.SendAsync(command, new(request, Guid.NewGuid()), token);
        InvokePluginAction Action(string id, string input) => new(session, Plugin, Version, id, Arguments(input));
        static ImmutableDictionary<string, string> Arguments(string input) => ImmutableDictionary<string, string>.Empty.Add("input", input);
        static string Issue(Outcome<PluginActionReceipt> outcome) => Assert.Single(Assert.IsType<Outcome<PluginActionReceipt>.Invalid>(outcome).Issues).Code;
    }

    /// <summary>Exercises panel admission, submit, and worker replacement through the composed workspace.</summary>
    /// <remarks>2026-11-25: Bind FR-PLUGINS-001#ac-visualize and TEST-PLUGINS-002 to real worker arguments.</remarks>
    [Theory]
    [InlineData("panel")]
    [InlineData("panel-no-action")]
    public async Task PanelBindsCurrentTrackAndRequiresTheNegotiatedAction(string mode)
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => new TestPlaybackOutput(),
            presentationScheduler: new InlineScheduler());
        var active = Success(await desktop.StartAsync(token));
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
        var models = Assert.IsType<DesktopPresentation>(active.Presentation);
        var packages = Assert.IsType<LocalPluginPackages>(active.PluginPackages);
        var session = Assert.IsType<Guid>(active.PlaybackSessionId);
        var selection = new PluginPanelSelection(new(Plugin, Version), "listening-notes");
        var query = new GetPluginPanel(session, Plugin, Version, selection.PanelId);
        Success(await Send(new InstallPluginPackage(packages.SelectPackage(CreateArchive(mode)))));
        Assert.Equal("plugin-panel-unavailable", Issue(await bus.QueryAsync(query, request, token)));
        Success(await Send(new SetPluginTrust(Plugin, Version, PluginTrust.Local)));
        Success(await Send(new SetPluginEnabled(Plugin, Version, true)));
        var ready = await ObserveAsync(models.Extensions, PluginWorkerStatus.Ready);
        if (mode == "panel-no-action")
        {
            Assert.Empty(Assert.Single(ready.Plugins).Contributions);
            Assert.False(models.Extensions.OpenPanelCommand.CanExecute(selection));
            Assert.Equal("plugin-panel-unavailable", Issue(await bus.QueryAsync(query, request, token)));
            return;
        }

        var track = Guid.NewGuid();
        const string note = "Live from the listening panel";
        Success(await Send(new EnqueueTracks(session, [track], EnqueueMode.PlayNow)));
        Assert.True(models.Extensions.OpenPanelCommand.CanExecute(selection));
        await models.Extensions.OpenPanelCommand.ExecuteAsync(selection);
        var panel = Assert.IsType<PluginPanelSnapshot>(models.Extensions.Panel);
        Assert.Equal("Listening Notes", panel.Definition.Title);
        Assert.Equal(new[] { "track", "note" }, panel.Definition.Fields.Select(field => field.Id));
        Assert.Equal(track, panel.Reference.TrackId);
        Assert.Equal(Assert.Single(ready.Plugins).ActivationId, panel.Reference.ActivationId);
        var fields = ImmutableDictionary<string, string>.Empty.Add("note", note);
        Assert.Equal("plugin-panel-context-required", Issue(await Send(new InvokePluginAction(session, Plugin, Version, "save-note", fields))));
        Assert.Equal("invalid-plugin-panel-fields", Issue(await Send(new SubmitPluginPanel(panel.Reference, fields.Add("track", track.ToString())))));
        Assert.Equal("invalid-plugin-panel-fields", Issue(await Send(new SubmitPluginPanel(panel.Reference, fields.SetItem("note", new string('x', 65))))));
        Assert.Equal("invalid-plugin-panel-fields", Issue(await Send(new SubmitPluginPanel(panel.Reference, fields.Remove("note")))));
        var worker = Assert.Single(Directory.EnumerateDirectories(Path.Combine(root.FullName, "plugins", "workers")));
        var marker = Path.Combine(worker, "panel-arguments.json");
        Assert.False(File.Exists(marker));

        Assert.True(models.Extensions.SubmitPanelCommand.CanExecute(fields));
        await models.Extensions.SubmitPanelCommand.ExecuteAsync(fields);
        Assert.Equal("Listening note saved", Assert.IsType<PluginActionReceipt>(models.Extensions.LastAction).Message);
        var markerText = await File.ReadAllTextAsync(marker, token);
        PluginPanelAssertions.VerifyInput(JsonSerializer.Deserialize<Dictionary<string, string>>(markerText)!, track, note);
        Success(await Send(new EnqueueTracks(session, [Guid.NewGuid()], EnqueueMode.PlayNow)));
        var staleTrack = Assert.IsType<Outcome<PluginActionReceipt>.Conflict>(await Send(new SubmitPluginPanel(panel.Reference, fields)));
        Assert.Equal("plugin-panel-track-changed", staleTrack.Code);
        Assert.Equal(markerText, await File.ReadAllTextAsync(marker, token));

        var restart = Success(await Send(new RestartPluginWorker(Plugin, Version)));
        var restarted = await ObserveAsync(models.Extensions, PluginWorkerStatus.Ready, restart.Revision);
        Assert.NotEqual(panel.Reference.ActivationId, Assert.Single(restarted.Plugins).ActivationId);
        Assert.Null(models.Extensions.Panel);
        Assert.Equal("stale-plugin-panel", Issue(await Send(new SubmitPluginPanel(panel.Reference, fields))));
        await models.Extensions.OpenPanelCommand.ExecuteAsync(selection);
        Assert.NotNull(models.Extensions.Panel);
        models.Extensions.ClosePanelCommand.Execute(null);
        Assert.Null(models.Extensions.Panel);

        Task<Outcome<T>> Send<T>(ICommand<T> command) => bus.SendAsync(command, new(request, Guid.NewGuid()), token);
        static string Issue<T>(Outcome<T> result) => Assert.Single(Assert.IsType<Outcome<T>.Invalid>(result).Issues).Code;
    }

    /// <summary>Verifies independent startup, projected failure, restart and recovery suppression in a composed desktop.</summary>
    /// <remarks>2026-11-25: Bind FR-PLUGINS-001#ac-recovery to a real worker exit, versioned diagnostics and responsive workspace routes.</remarks>
    /// <remarks>2026-11-26: Use the real worker fixture in the named FR-PLUGINS-001#ac-recovery case.</remarks>
    internal async Task WorkspaceSupervisesWorkerExitRestartAndRecovery()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var archive = CreateArchive("report-pid");
        var workers = Path.Combine(root.FullName, "plugins", "workers");
        await using (var desktop = Open())
        {
            var active = Success(await desktop.StartAsync(token));
            var route = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
            var packages = Assert.IsType<LocalPluginPackages>(active.PluginPackages);
            var presentation = Assert.IsType<DesktopPresentation>(active.Presentation);
            var session = Assert.IsType<Guid>(active.PlaybackSessionId);
            Success(await Send(route, new InstallPluginPackage(packages.SelectPackage(archive))));
            Success(await Send(route, new SetPluginTrust(Plugin, Version, PluginTrust.Local)));
            Assert.False(Directory.Exists(workers));
            var accepted = Success(await Send(route, new SetPluginEnabled(Plugin, Version, true)));
            Assert.Equal(PluginWorkerStatus.Starting, Assert.Single(accepted.Plugins).Worker);
            var ready = await ObserveAsync(presentation.Extensions, PluginWorkerStatus.Ready);
            var track = Guid.NewGuid();
            var playing = Success(await Send(route, new EnqueueTracks(session, [track], EnqueueMode.PlayNow)));
            Assert.Equal(TransportStatus.Playing, playing.Status);
            var workerDirectory = Assert.Single(Directory.EnumerateDirectories(workers));
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(workerDirectory, "worker.pid"), token), CultureInfo.InvariantCulture);
            using (var worker = Process.GetProcessById(pid))
            {
                Assert.False(worker.HasExited);
                worker.Kill();
                await worker.WaitForExitAsync(token);
            }
            var failed = await ObserveAsync(presentation.Extensions, PluginWorkerStatus.Failed);
            PluginWorkerAssertions.VerifyExit(ready, failed);
            Assert.Empty(Directory.EnumerateDirectories(workers));
            var logs = Success(await route.QueryAsync(new GetDiagnostics(new("Desktop.Plugins", PluginId: Plugin)), request, token));
            Assert.Contains(logs.Entries, item => item.PluginId == Plugin && item.PluginVersion == Version && item.Code == "5401");
            var navigation = Success(await Send(route, new NavigateWorkspace(presentation.Shell.Scope.OwnerId, new(Workspace.Sync))));
            Assert.Equal(Workspace.Sync, navigation.Route.Workspace);
            var uninterrupted = Success(await route.QueryAsync(new GetPlaybackState(session), request, token));
            Assert.Equal(TransportStatus.Playing, uninterrupted.Status);
            Assert.Equal(track, uninterrupted.ActiveTrackId);
            Assert.Equal(TransportStatus.Paused, Success(await Send(route, new Transport(session, TransportAction.Pause))).Status);
            var restart = Success(await Send(route, new RestartPluginWorker(Plugin, Version)));
            Assert.Equal(PluginWorkerStatus.Starting, Assert.Single(restart.Plugins).Worker);
            await ObserveAsync(presentation.Extensions, PluginWorkerStatus.Ready, restart.Revision);
            var disabled = Success(await Send(route, new SetPluginEnabled(Plugin, Version, false)));
            PluginRegistryAssertions.VerifyInstalled(disabled, Plugin, Version, PluginTrust.Local, false);
            var reenabled = Success(await Send(route, new SetPluginEnabled(Plugin, Version, true)));
            await ObserveAsync(presentation.Extensions, PluginWorkerStatus.Ready, reenabled.Revision);
            Success(await desktop.StopAsync(token));
            Assert.Empty(Directory.EnumerateDirectories(workers));
        }

        await using (var recovery = Open(recoveryMode: true))
        {
            var active = Success(await recovery.StartAsync(token));
            var route = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
            var state = Success(await route.QueryAsync(new GetPluginState(), request, token));
            Assert.True(state.RecoveryMode);
            PluginRegistryAssertions.VerifyInstalled(state, Plugin, Version, PluginTrust.Local, true);
            var restart = Assert.IsType<Outcome<PluginState>.Invalid>(await Send(route, new RestartPluginWorker(Plugin, Version)));
            Assert.Equal("plugin-workers-disabled", Assert.Single(restart.Issues).Code);
            Assert.Empty(Directory.EnumerateDirectories(workers));
        }

        await using var normal = Open();
        var resumed = Assert.IsType<DesktopPresentation>(Success(await normal.StartAsync(token)).Presentation);
        Assert.False((await ObserveAsync(resumed.Extensions, PluginWorkerStatus.Ready)).RecoveryMode);

        DesktopStartup Open(bool recoveryMode = false) => new(settings, [], library, origin, _ => new TestPlaybackOutput(),
            presentationScheduler: new InlineScheduler(), pluginRecoveryMode: recoveryMode);
        Task<Outcome<T>> Send<T>(IRequestDispatcher route, ICommand<T> command) =>
            route.SendAsync(command, new(request, Guid.NewGuid()), token);
    }

    private async Task<PluginState> ObserveAsync(ExtensionsViewModel viewModel, PluginWorkerStatus expected, long afterRevision = -1)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (viewModel.State is { } snapshot && snapshot.Revision > afterRevision && snapshot.Plugins.Any(item => item.Worker == expected))
                return snapshot;
            await Task.Delay(15, token);
        }
        throw new TimeoutException($"The desktop did not project {expected}.");
    }

    /// <summary>Exercises the built-in and exact negotiated worker visualizations in a headless workspace.</summary>
    /// <remarks>2026-11-25: Bind FR-PLUGINS-001#ac-visualize and ac-recovery, and TEST-PLUGINS-002, to real replies and a timeout.</remarks>
    /// <remarks>2026-11-26: Use the real worker fixture in the named FR-PLUGINS-001#ac-visualize case.</remarks>
    internal async Task WorkerVisualizationUsesFreshAudioWhilePlaybackRemainsResponsive()
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        using var output = new VisualizationPlaybackOutput();
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => output, presentationScheduler: new InlineScheduler());
        var active = Success(await desktop.StartAsync(token));
        var route = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
        var models = Assert.IsType<DesktopPresentation>(active.Presentation);
        var packages = Assert.IsType<LocalPluginPackages>(active.PluginPackages);
        var session = Assert.IsType<Guid>(active.PlaybackSessionId);
        var extensions = models.Extensions;
        var projected = new System.Collections.Concurrent.ConcurrentQueue<VisualizationState>();
        extensions.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ExtensionsViewModel.Visualization) && extensions.Visualization is { } snapshot)
                projected.Enqueue(snapshot);
        };
        var plugin = new VisualizationChoice("levels", Plugin, Version);
        await extensions.OpenVisualizationPickerCommand.ExecuteAsync(null);
        Assert.Equal(VisualizationChoice.Spectrum, extensions.Visualization?.Choice);
        Assert.False(extensions.SelectVisualizationCommand.CanExecute(plugin));
        Success(await Send(new InstallPluginPackage(packages.SelectPackage(CreateArchive("visualization")))));
        Success(await Send(new SetPluginTrust(Plugin, Version, PluginTrust.Local)));
        Success(await Send(new SetPluginEnabled(Plugin, Version, true)));
        var ready = await ObserveAsync(extensions, PluginWorkerStatus.Ready);
        Assert.Equal("levels", Assert.Single(Assert.Single(ready.Plugins).Visualizations));
        Assert.Equal("plugin-visualization-unavailable", Assert.Single(Assert.IsType<Outcome<VisualizationState>.Invalid>(
            await Send(new SelectVisualization(session, plugin with { Version = "2.0.0" }))).Issues).Code);
        var worker = Assert.Single(Directory.EnumerateDirectories(Path.Combine(root.FullName, "plugins", "workers")));
        var track = Guid.NewGuid();
        Success(await Send(new EnqueueTracks(session, [track], EnqueueMode.PlayNow)));
        await extensions.SelectVisualizationCommand.ExecuteAsync(VisualizationChoice.Waveform);
        await Task.Delay(125, token);
        var waveSequence = output.Publish();
        await UntilAsync(() => extensions.Visualization?.Frame?.Sequence == waveSequence);
        var waveform = Assert.IsType<VisualizationState>(extensions.Visualization);
        Assert.Equal(-0.25f, waveform.Frame!.Values[0]);
        Assert.Empty(Directory.EnumerateFiles(worker, "visualization-*.json"));

        Assert.True(extensions.SelectVisualizationCommand.CanExecute(plugin));
        await extensions.SelectVisualizationCommand.ExecuteAsync(plugin);
        Assert.Null(extensions.Visualization?.Frame);
        await Task.Delay(125, token);
        var pluginSequence = output.Publish();
        await UntilAsync(() => extensions.Visualization?.Frame?.Sequence == pluginSequence);
        var displayed = Assert.IsType<VisualizationState>(extensions.Visualization);
        using (var message = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(worker, "visualization-1.json"), token)))
        {
            Assert.Equal(session, message.RootElement.GetProperty("sessionId").GetGuid());
            Assert.Equal(pluginSequence, message.RootElement.GetProperty("sequence").GetInt64());
        }

        output.Publish();
        await UntilAsync(() => File.Exists(Path.Combine(worker, "visualization-2.json")));
        await Task.Delay(180, token);
        output.Publish();
        await UntilAsync(() => File.Exists(Path.Combine(worker, "visualization-3.json")));
        var paused = Success(await Send(new Transport(session, TransportAction.Pause)).WaitAsync(TimeSpan.FromSeconds(1), token));
        Assert.Null(Success(await route.QueryAsync(new GetVisualization(session), context, token)).Frame);
        await UntilAsync(() => extensions.Visualization?.DetachedReason is not null);
        var detached = Assert.IsType<VisualizationState>(extensions.Visualization);
        Assert.Null(detached.Frame);
        var staleWasProjected = projected.Any(snapshot => snapshot.Choice == plugin && snapshot.Frame is { } frame &&
            (frame.Sequence != pluginSequence || !frame.Values.SequenceEqual(displayed.Frame!.Values)));
        var stable = Success(await route.QueryAsync(new GetPlaybackState(session), context, token));
        VisualizationWorkerAssertions.Verify(new(waveform.Choice.ContributionId, waveform.Frame.Sequence,
            $"{displayed.Choice.PluginId}/{displayed.Choice.Version}/{displayed.Choice.ContributionId}",
            displayed.Frame?.Sequence, displayed.Frame!.Values, staleWasProjected, detached.DetachedReason!,
            paused.Status == TransportStatus.Paused && stable.Status == TransportStatus.Paused && stable.ActiveTrackId == track));
        await ObserveAsync(extensions, PluginWorkerStatus.Failed);
        await extensions.SelectVisualizationCommand.ExecuteAsync(VisualizationChoice.Spectrum);
        Assert.Equal(VisualizationChoice.Spectrum, extensions.Visualization?.Choice);
        Assert.Null(extensions.Visualization?.DetachedReason);

        Task<Outcome<T>> Send<T>(ICommand<T> command) => route.SendAsync(command, new(context, Guid.NewGuid()), token);
        async Task UntilAsync(Func<bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task ApprovedWorkerReturnsOnlyTheAdvertisedSubsetAndExposesItsExit()
    {
        var (factory, package) = await PrepareAsync("exit-after-ready");
        var denied = await factory.StartAsync(package with { Trust = PluginTrust.Untrusted }, token);
        Assert.Equal("plugin-worker-not-authorized", Assert.Single(Assert.IsType<Outcome<IPluginWorker>.Invalid>(denied).Issues).Code);
        var workers = Path.Combine(root.FullName, "plugins", "workers");
        Assert.False(Directory.Exists(workers));
        using (var worker = Success(await factory.StartAsync(package, token)))
        {
            PluginWorkerAssertions.Verify(new(Plugin, Version, package.Trust, package.Enabled, PluginWorkerStatus.Ready, worker.Contributions),
                Plugin, Version, PluginWorkerStatus.Ready, null, new PluginContribution("levels", PluginContributionKind.Visualization));
            Assert.Empty(worker.Visualizations);
            Assert.Single(Directory.EnumerateDirectories(workers));
            for (var attempt = 0; attempt < 200 && !worker.HasExited; attempt++)
                await Task.Delay(15, token);
            Assert.True(worker.HasExited);
        }
        Assert.Empty(Directory.EnumerateDirectories(workers));
    }

    [Theory]
    [InlineData("wrong-id", "plugin-worker-protocol-invalid")]
    [InlineData("wrong-version", "plugin-worker-protocol-invalid")]
    [InlineData("undeclared", "plugin-worker-protocol-invalid")]
    [InlineData("malformed", "plugin-worker-protocol-invalid")]
    [InlineData("oversized", "plugin-worker-protocol-invalid")]
    [InlineData("timeout", "plugin-worker-timeout")]
    [InlineData("exit", "plugin-worker-exited")]
    public async Task InvalidHandshakeRejectsContributionsAndCleansUp(string mode, string code)
    {
        var (factory, package) = await PrepareAsync(mode);
        var failure = Assert.IsType<Outcome<IPluginWorker>.DependencyFailure>(await factory.StartAsync(package, token));
        PluginWorkerAssertions.Verify(new(Plugin, Version, package.Trust, package.Enabled, PluginWorkerStatus.Failed, [], failure.Code),
            Plugin, Version, PluginWorkerStatus.Failed, code);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root.FullName, "plugins", "workers")));
    }

    [Fact]
    public async Task AlteredPrivateArchiveCannotStartUnderOldTrust()
    {
        var (factory, package) = await PrepareAsync("ready");
        var blob = Path.Combine(root.FullName, "plugins", "archives", $"{package.Package.ArchiveSha256}.zip");
        await File.AppendAllTextAsync(blob, "altered cached bytes", token);
        Assert.Equal("plugin-archive-digest-mismatch",
            Assert.IsType<Outcome<IPluginWorker>.DependencyFailure>(await factory.StartAsync(package, token)).Code);
        Assert.False(Directory.Exists(Path.Combine(root.FullName, "plugins", "workers")));
    }

    private async Task<(PluginWorkerFactory Factory, InstalledPluginPackage Version)> PrepareAsync(string mode)
    {
        var settings = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        await using var desktop = new DesktopStartup(settings, [], Guid.NewGuid(), origin, _ => new UnusedOutput(), pluginRecoveryMode: true);
        var active = Success(await desktop.StartAsync(token));
        var bus = Assert.IsAssignableFrom<IRequestDispatcher>(active.Workspace);
        var selected = Assert.IsType<LocalPluginPackages>(active.PluginPackages);
        var archive = CreateArchive(mode);
        Success(await Send(new InstallPluginPackage(selected.SelectPackage(archive))));
        Success(await Send(new SetPluginTrust(Plugin, Version, PluginTrust.Local)));
        Success(await Send(new SetPluginEnabled(Plugin, Version, true)));
        Success(await desktop.StopAsync(token));
        using var store = LiteDbDesktopPlaylistStore.Open(settings.DatabasePath, allowCreate: false);
        var installed = Assert.Single(Success(await store.ReadPluginsAsync(token)).Versions);
        return (new(selected), installed);

        Task<Outcome<PluginState>> Send(ICommand<PluginState> command) =>
            bus.SendAsync(command, new(request, Guid.NewGuid()), token);
    }

    private string CreateArchive(string mode)
    {
        var assembly = typeof(WorkerFixtureAssembly).Assembly.Location;
        var binary = Path.GetFileName(assembly);
        var path = Path.Combine(root.FullName, "selected.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifest = zip.CreateEntry("plugin.json").Open())
            JsonSerializer.Serialize(manifest, new
            {
                pluginId = Plugin, version = Version, minimumProtocol = 1, maximumProtocol = 1, entryPoint = $"bin/{binary}",
                contributions = mode switch
                {
                    "actions" => new object[] { new { contributionId = "pause", kind = "action", hostAction = PluginHostActions.PausePlayback },
                        new { contributionId = "note", kind = "action" } },
                    "panel" or "panel-no-action" => new object[]
                    {
                        new { contributionId = "save-note", kind = "action" },
                        new
                        {
                            contributionId = "listening-notes", kind = "panel",
                            panel = new
                            {
                                title = "Listening Notes", actionId = "save-note",
                                fields = new[]
                                {
                                    new { id = "track", label = "Current track", kind = "currentTrack", required = true, maximumLength = 0 },
                                    new { id = "note", label = "Listening note", kind = "text", required = true, maximumLength = 64 }
                                }
                            }
                        }
                    },
                    _ => new object[] { new { contributionId = "levels", kind = "visualization" }, new { contributionId = "detail", kind = "panel" } }
                }
            });
        using (var behavior = new StreamWriter(zip.CreateEntry("mode.txt").Open()))
            behavior.Write(mode);
        zip.CreateEntryFromFile(assembly, $"bin/{binary}");
        foreach (var extension in new[] { ".runtimeconfig.json", ".deps.json" })
        {
            var hosting = Path.ChangeExtension(assembly, extension);
            zip.CreateEntryFromFile(hosting, $"bin/{Path.GetFileName(hosting)}");
        }
        return path;
    }

    private static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    public void Dispose() => root.Delete(recursive: true);

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private class TestPlaybackOutput : IDesktopPlaybackOutput
    {
        private DeckState? state;
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            state = new(deck, track, position ?? state?.Position ?? TimeSpan.Zero, TimeSpan.FromSeconds(30), status, true, 1);
            return ReadAsync(deck, track, token);
        }
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid track, CancellationToken token) =>
            Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(state!));
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken token) =>
            ChangeAsync(deck, track, position, state!.Status, token);
        public void Dispose() { }
    }

    /// <summary>Supplies deterministic windows at the real desktop composition boundary.</summary>
    private sealed class VisualizationPlaybackOutput : TestPlaybackOutput, IPlaybackVisualizationSource
    {
        private long sequence;
        private PlaybackVisualizationFrame? latest;

        internal long Publish()
        {
            var next = ++sequence;
            Volatile.Write(ref latest, new(next, TimeProvider.System.GetTimestamp(), 48000,
                [.. Enumerable.Repeat(-0.25f, 64)], [.. Enumerable.Repeat(0.375f, 32)]));
            return next;
        }

        public PlaybackVisualizationFrame? ReadLatest(long afterSequence) =>
            Volatile.Read(ref latest) is { } frame && frame.Sequence > afterSequence ? frame : null;
    }

    private sealed class UnusedOutput : IDesktopPlaybackOutput
    {
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token) =>
            throw new InvalidOperationException("Plugin negotiation does not touch playback.");
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) =>
            throw new InvalidOperationException("Plugin negotiation does not touch playback.");
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
            throw new InvalidOperationException("Plugin negotiation does not touch playback.");
        public void Dispose() { }
    }
}
