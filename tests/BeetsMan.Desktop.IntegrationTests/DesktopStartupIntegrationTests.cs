using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using System.Text.Json;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises bootstrap, cached browsing, and resume through the desktop lifecycle entry point.</summary>
/// <remarks>2026-11-25: Bind disposable JSON, LiteDB and a controlled audio output for FR-CONFIG-001#ac-defaults, FR-CONFIG-001#ac-repair and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
public sealed class DesktopStartupIntegrationTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-desktop-startup-");

    /// <summary>Uses the same first-run ViewModel to apply defaults or a reviewed custom path and open the active workspace.</summary>
    /// <remarks>2026-11-25: Bind FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair to a real appsettings file and LiteDB.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupChoiceOpensWorkspaceWithWaitStateThroughHandoff(bool customDatabase)
    {
        var token = TestContext.Current.CancellationToken;
        var defaults = JsonBootstrapFile.ResolveDefaults(directory.FullName, Path.Combine(directory.FullName, "music"));
        var scheduler = new PausableScheduler();
        var openCount = 0;
        await using var startup = new DesktopStartup(defaults, [], Guid.NewGuid(), Guid.NewGuid(), _ =>
        {
            openCount++;
            return new ControlledAudio();
        }, presentationScheduler: scheduler);
        var setup = Assert.IsType<DesktopSetupPresentation>(startup.Setup);
        var configuration = setup.Configuration;
        await configuration.RetryCommand.ExecuteAsync(null);
        var waited = configuration.State?.Status == BootstrapStatus.FirstRun && startup.ActiveWorkspace is null &&
            File.Exists(defaults.SettingsPath) == false && File.Exists(defaults.DatabasePath) == false && openCount == 0;
        var expected = customDatabase ? Path.Combine(directory.FullName, "custom-library.db") : defaults.DatabasePath;
        var approvalSatisfied = true;
        if (customDatabase)
        {
            await configuration.EditLocationsCommand.ExecuteAsync(null);
            configuration.ProposedSettings = Assert.IsType<BootstrapSettings>(configuration.ProposedSettings) with { DatabasePath = expected };
            await configuration.ApplyLocationsCommand.ExecuteAsync(null);
            var approval = Assert.IsType<DatabaseCreationApproval>(configuration.State?.CreationProposal);
            configuration.DatabaseApproval = approval with { DatabasePath = defaults.DatabasePath };
            await configuration.ApplyLocationsCommand.ExecuteAsync(null);
            approvalSatisfied = approval.DatabasePath == expected && configuration.State?.CreationProposal == approval &&
                !File.Exists(expected) && !File.Exists(defaults.DatabasePath) && startup.ActiveWorkspace is null;
            configuration.DatabaseApproval = approval;
        }

        scheduler.PauseNext();
        var initializing = (customDatabase ? configuration.ApplyLocationsCommand : configuration.UseDefaultsCommand).ExecuteAsync(null);
        bool blocked;
        try
        {
            await scheduler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            blocked = configuration.IsInteractionBlocked && startup.ActiveWorkspace is null && openCount == 1;
        }
        finally { scheduler.Release.TrySetResult(); }
        await initializing;
        var active = Assert.IsType<DesktopStartupState>(startup.ActiveWorkspace);
        var opened = configuration.State is { Status: BootstrapStatus.Ready, Effective.DatabasePath: var path } && path == expected &&
            File.Exists(defaults.SettingsPath) && File.Exists(expected) && active.Presentation is not null && active.Workspace is not null;
        var released = !configuration.IsInteractionBlocked && !configuration.IsWriting;
        await configuration.RetryCommand.ExecuteAsync(null);
        var reused = ReferenceEquals(active, startup.ActiveWorkspace) && openCount == 1;

        await configuration.EditLocationsCommand.ExecuteAsync(null);
        var newMusic = Directory.CreateDirectory(Path.Combine(directory.FullName, "new-music")).FullName;
        configuration.ProposedSettings = Assert.IsType<BootstrapSettings>(configuration.ProposedSettings) with { MusicRoot = newMusic };
        await configuration.ApplyLocationsCommand.ExecuteAsync(null);
        var restart = configuration.State is { Status: BootstrapStatus.RestartRequired, Effective.MusicRoot: var oldRoot,
            Proposed.MusicRoot: var proposed } && oldRoot == defaults.MusicRoot && proposed == newMusic &&
            ReferenceEquals(active, startup.ActiveWorkspace) && openCount == 1;
        await startup.DisposeAsync();
        var disposed = !configuration.IsActive && !setup.Diagnostics.IsActive && startup.ActiveWorkspace is null;
        DesktopSetupPresentationAssertions.Verify(new(waited, approvalSatisfied, blocked, opened, released, reused, restart, disposed));
    }

    /// <summary>Drives the offline Library, queue, playlist editor and batch review from one started workspace.</summary>
    /// <remarks>2026-11-25: Bind real local state through CQRS for FR-LIBRARY-001#ac-browse, ac-offline and FR-LIBRARY-002#ac-batch-local, ac-batch-review and ac-batch-revert.</remarks>
    [Fact]
    public async Task ComposedLibraryProjectsQueueAndNewPlaylistChangesIntoBatchReview()
    {
        var token = TestContext.Current.CancellationToken;
        var defaults = JsonBootstrapFile.ResolveDefaults(directory.FullName, Path.Combine(directory.FullName, "music"));
        Directory.CreateDirectory(defaults.MusicRoot);
        using (LiteDbDesktopPlaylistStore.Open(defaults.DatabasePath, allowCreate: true)) { }
        var library = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (var seed = new LiteDB.LiteDatabase(defaults.DatabasePath))
        {
            foreach (var (track, name) in new[] { (first, "First"), (second, "Second") })
            {
                var file = name + ".flac";
                File.WriteAllText(Path.Combine(defaults.MusicRoot, file), "cached audio fixture");
                seed.GetCollection<LiteDB.BsonDocument>("catalog_tracks").Insert(new LiteDB.BsonDocument
                {
                    ["_id"] = track, ["LibraryId"] = library, ["Title"] = name, ["SourceRevision"] = 42L, ["RelativePath"] = file
                });
            }
        }

        await using var startup = new DesktopStartup(defaults, [], library, Guid.NewGuid(), _ => new ControlledAudio(),
            presentationScheduler: new ImmediateScheduler());
        var models = Assert.IsType<DesktopPresentation>(Success(await startup.StartAsync(token)).Presentation);
        var startedBatch = models.LibraryBatch.Mode is { IsEnabled: false, ChangeCount: 0 } initial &&
            models.HostLibrary.BatchMode == initial && !models.LibraryBatch.OpenReviewCommand.CanExecute(null);
        await models.Library.BrowseCommand.ExecuteAsync(new CatalogBrowseInput(CatalogCategory.Tracks, new(), new(CatalogSortKey.Title)));
        var catalog = Assert.IsType<CatalogPage>(models.Library.Page);
        var browsed = catalog.IsOffline && catalog.Rows.Select(row => row.Id).SequenceEqual([first, second]);
        var copyQueueCommandWasDisabled = !models.PlaylistDraft.CreateFromQueueCommand.CanExecute(null);
        var copyQueueCommandChanged = false;
        models.PlaylistDraft.CreateFromQueueCommand.CanExecuteChanged += (_, _) =>
            copyQueueCommandChanged |= models.PlaylistDraft.CreateFromQueueCommand.CanExecute(null);
        await models.Library.AddToQueueCommand.ExecuteAsync([first, second]);
        var queue = Assert.IsType<QueueSnapshot>(models.Queue.Queue);
        await models.Queue.ReorderCommand.ExecuteAsync([.. queue.Entries.Reverse().Select(entry => entry.EntryId)]);
        var shared = models.Queue.Queue == models.Playback.State?.Queue &&
            models.Queue.Queue?.Entries.Select(entry => entry.TrackId).SequenceEqual([second, first]) == true;
        await models.PlaylistDraft.CreateFromQueueCommand.ExecuteAsync(null);
        var draft = Assert.IsType<PlaylistDraft>(models.PlaylistDraft.Draft);
        var copied = copyQueueCommandWasDisabled && copyQueueCommandChanged &&
            draft.Entries.Select(entry => entry.TrackId).SequenceEqual([second, first]);

        await models.LibraryBatch.SetModeCommand.ExecuteAsync(true);
        models.PlaylistDraft.Name = "First reviewed playlist";
        await models.PlaylistDraft.SaveCommand.ExecuteAsync(null);
        var staged = Assert.IsType<LibraryBatchStageReceipt>(models.PlaylistDraft.StagedSave);
        var enabled = models.LibraryBatch.OpenReviewCommand.CanExecute(null) &&
            models.LibraryBatch.Mode is { ChangeCount: 1 } mode && mode.Revision == staged.Version.Revision &&
            models.HostLibrary.BatchMode == mode && models.PlaylistDraft.Saved is null;
        await models.LibraryBatch.OpenReviewCommand.ExecuteAsync(null);
        var review = Assert.IsType<LibraryBatchReview>(models.LibraryBatch.Review);
        var entry = Assert.Single(review.Entries);
        var listed = entry.OperationId == staged.OperationId && review.Version == staged.Version &&
            entry.Change is LibraryBatchChange.Playlist { Name: "First reviewed playlist" } playlist &&
            playlist.Entries.Select(item => item.TrackId).SequenceEqual([second, first]);

        models.PlaylistDraft.Name = "Second reviewed playlist";
        await models.PlaylistDraft.SaveCommand.ExecuteAsync(null);
        var invalidated = models.LibraryBatch.Mode?.ChangeCount == 2 && models.LibraryBatch.Review is null &&
            !models.LibraryBatch.CommitCommand.CanExecute(false);
        await models.LibraryBatch.RevertCommand.ExecuteAsync(false);
        var cleared = models.LibraryBatch.Mode is { IsEnabled: false, ChangeCount: 0 } &&
            models.HostLibrary.BatchMode == models.LibraryBatch.Mode && models.PlaylistDraft.StagedSave is null &&
            models.PlaylistDraft.Draft?.DraftId == draft.DraftId && models.PlaylistDraft.Saved is null;

        Success(await startup.StopAsync(token));
        var disposed = new PresentationViewModel[] { models.Library, models.Queue, models.PlaylistDraft, models.LibraryBatch,
            models.TagEdit, models.ActionError }.All(model => !model.IsActive);
        LibraryWorkspacePresentationAssertions.Verify(new(browsed, shared, copied, startedBatch, enabled, listed, invalidated, cleared, disposed));
    }

    /// <summary>Reads a redacted startup issue through the normal diagnostics command during recovery.</summary>
    /// <remarks>2026-11-25: Bind a real malformed settings file and isolated repair for FR-CONFIG-001#ac-repair, ac-logs and TEST-CONFIG-002.</remarks>
    [Fact]
    public async Task RecoveryDiagnosticsLoadWithoutOpeningTheWorkspace()
    {
        var token = TestContext.Current.CancellationToken;
        var defaults = JsonBootstrapFile.ResolveDefaults(directory.FullName, Path.Combine(directory.FullName, "music"));
        var origin = Guid.NewGuid();
        var openedOutputs = 0;
        await using var startup = new DesktopStartup(defaults, [], Guid.NewGuid(), origin, _ =>
        {
            openedOutputs++;
            return new ControlledAudio();
        }, presentationScheduler: new ImmediateScheduler());
        const string privateMarker = "fixture-private-value";
        const string malformed = "{ \"privateKey\": \"" + privateMarker + "\",";
        await File.WriteAllTextAsync(defaults.SettingsPath, malformed, token);
        var setup = Assert.IsType<DesktopSetupPresentation>(startup.Setup);
        await setup.Configuration.RetryCommand.ExecuteAsync(null);
        var blocked = setup.Configuration.State?.Status == BootstrapStatus.RepairRequired && startup.ActiveWorkspace is null &&
            openedOutputs == 0 && !File.Exists(defaults.DatabasePath) && !setup.Diagnostics.OpenLogFolderCommand.CanExecute(null);
        var sourcePreserved = await File.ReadAllTextAsync(defaults.SettingsPath, token) == malformed;
        var noFilesWritten = !Directory.Exists(defaults.RollingLog!.Directory);

        var model = setup.Diagnostics;
        await model.LoadCommand.ExecuteAsync(null);
        var all = Assert.IsType<DiagnosticsPage>(model.Page);
        var warning = Assert.Single(all.Entries);
        var correlation = Assert.IsType<Guid>(warning.CorrelationId);
        model.Filter = new(Category: "Desktop.Bootstrap", CorrelationId: correlation);
        await model.LoadCommand.ExecuteAsync(null);
        var matching = Assert.IsType<DiagnosticsPage>(model.Page).Entries.SequenceEqual(all.Entries);
        model.Filter = new(CorrelationId: Guid.NewGuid());
        await model.LoadCommand.ExecuteAsync(null);
        var otherEmpty = Assert.IsType<DiagnosticsPage>(model.Page).Entries.IsEmpty;

        await File.WriteAllTextAsync(defaults.SettingsPath, "{}", token);
        using (LiteDbDesktopPlaylistStore.Open(defaults.DatabasePath, allowCreate: true)) { }
        await setup.Configuration.RetryCommand.ExecuteAsync(null);
        var repaired = startup.ActiveWorkspace;
        RecoveryDiagnosticsAssertions.Verify(new(blocked, sourcePreserved,
            warning is { Category: "Desktop.Bootstrap", Level: "Warning", PluginId: null } && !string.IsNullOrWhiteSpace(warning.Code),
            matching, otherEmpty, !JsonSerializer.Serialize(all).Contains(privateMarker, StringComparison.Ordinal),
            repaired is { Bootstrap.Status: BootstrapStatus.Ready, Workspace: not null } && openedOutputs == 1, noFilesWritten));
    }

    [Fact]
    public async Task StartupUsesOneWorkspaceAndCheckpointsBeforeClosingAudio()
    {
        var token = TestContext.Current.CancellationToken;
        var defaults = JsonBootstrapFile.ResolveDefaults(directory.FullName, Path.Combine(directory.FullName, "missing-music"));
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var track = Guid.NewGuid();
        var firstEntry = new QueueEntry(Guid.NewGuid(), track);
        var finalEntry = new QueueEntry(Guid.NewGuid(), track);
        var outputs = new List<ControlledAudio>();
        DesktopStartup Open() => new(defaults, [], library, origin, _ =>
        {
            var output = new ControlledAudio();
            outputs.Add(output);
            return output;
        });
        await using var startup = Open();
        await File.WriteAllTextAsync(defaults.SettingsPath, "{broken", token);
        var repair = Success(await startup.StartAsync(token));
        var blocked = repair is { Bootstrap.Status: BootstrapStatus.RepairRequired, Workspace: null } &&
            outputs.Count == 0 && !File.Exists(defaults.DatabasePath);
        File.Delete(defaults.SettingsPath);

        var initial = Success(await startup.StartAsync(token));
        var repeated = Success(await startup.StartAsync(token));
        var sessionId = Assert.IsType<Guid>(initial.PlaybackSessionId);
        var initialResult = Assert.IsType<Outcome<PlaybackState>.Success>(initial.Playback).Value;
        var defaultFiles = initial.Bootstrap.Status == BootstrapStatus.Ready && initialResult.Status == TransportStatus.Stopped &&
            File.Exists(defaults.SettingsPath) && File.Exists(defaults.DatabasePath);
        var cachedCatalog = Success(await initial.Workspace!.QueryAsync(new BrowseCatalog(CatalogCategory.Tracks, new(),
            new(CatalogSortKey.Title)), request, token));
        var browseOffline = cachedCatalog.IsOffline && cachedCatalog.Rows.IsEmpty;
        var reused = ReferenceEquals(initial.Workspace, repeated.Workspace) && repeated.PlaybackSessionId == sessionId && outputs.Count == 1;
        Success(await initial.Workspace.SendAsync(new SetLocalOverride(PlaybackPreferences.ResumeAfterRestart, new PreferenceValue.Flag(true), 0),
            new(request, Guid.NewGuid()), token));
        Success(await startup.StopAsync(token));

        using (var store = LiteDbDesktopPlaylistStore.Open(defaults.DatabasePath, false))
        {
            var checkpoint = new PlaybackCheckpoint(sessionId, 1, 2, [firstEntry, finalEntry], finalEntry.EntryId,
                TimeSpan.FromMilliseconds(800), false, RepeatMode.Off, new(new(Guid.NewGuid(), 3), "Lifecycle fixture"), WasPlaying: true);
            Success(await store.SaveCheckpointAsync(checkpoint, 0, token));
        }

        await using var restart = Open();
        var started = Success(await restart.StartAsync(token));
        var resumed = Assert.IsType<Outcome<PlaybackState>.Success>(started.Playback).Value;
        var audio = outputs[1];
        Assert.Equal(new[] { "start" }, audio.Events);
        audio.Position = TimeSpan.FromMilliseconds(1200);
        audio.ReadFails = true;
        var failedStop = await restart.StopAsync(token);
        var keptOpen = failedStop is Outcome<PlaybackState?>.DependencyFailure { Code: "fixture-read-failed", Retryable: true }
            && audio.DisposeCount == 0;
        audio.ReadFails = false;
        Success(await restart.StopAsync(token));
        Success(await restart.StopAsync(token));
        using var reopened = LiteDbDesktopPlaylistStore.Open(defaults.DatabasePath, false);
        var saved = Assert.IsType<PlaybackCheckpoint>(Success(await reopened.ReadCheckpointAsync(sessionId, token)));
        var savedBeforeClose = audio.Events.SequenceEqual(["start", "read-failed", "read", "dispose"]) && saved.WasPlaying &&
            saved.Position == TimeSpan.FromMilliseconds(1200);
        DesktopStartupAssertions.Verify(new(blocked, defaultFiles, browseOffline, reused, started.PlaybackSessionId == sessionId,
            resumed.Status == TransportStatus.Playing && resumed.ActiveQueueEntryId == finalEntry.EntryId &&
                resumed.Queue.Entries.SequenceEqual([firstEntry, finalEntry]), resumed.Position.Ticks,
            keptOpen, savedBeforeClose, outputs.Count == 2 && outputs.All(output => output.DisposeCount == 1)));
    }

    public void Dispose() => directory.Delete(recursive: true);

    private static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    private sealed class ImmediateScheduler : IUiScheduler
    {
        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            apply();
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the next scheduler callback so the test can observe a logically blocked setup.</summary>
    /// <remarks>2026-11-25: Observe the wait-pointer state for FR-CONFIG-001#ac-repair during workspace activation.</remarks>
    private sealed class PausableScheduler : IUiScheduler
    {
        private int pause;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void PauseNext() => Interlocked.Exchange(ref pause, 1);

        public async Task PostAsync(Action apply, CancellationToken token)
        {
            if (Interlocked.Exchange(ref pause, 0) != 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            token.ThrowIfCancellationRequested();
            apply();
        }
    }

    private sealed class ControlledAudio : IDesktopPlaybackOutput
    {
        internal readonly List<string> Events = [];
        internal int DisposeCount { get; private set; }
        internal bool ReadFails { get; set; }
        internal TimeSpan Position { get; set; }

        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? position, TransportStatus status, CancellationToken token)
        {
            Events.Add("start");
            Position = position ?? Position;
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.Success(new(deck, trackId, Position,
                TimeSpan.FromSeconds(3), status, true, 1)));
        }

        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token)
        {
            Events.Add(ReadFails ? "read-failed" : "read");
            return Task.FromResult<Outcome<DeckState>>(ReadFails ? new Outcome<DeckState>.DependencyFailure("fixture-read-failed", true) :
                new Outcome<DeckState>.Success(new(deck, trackId, Position, TimeSpan.FromSeconds(3), TransportStatus.Playing, true, 1)));
        }

        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) =>
            throw new NotSupportedException();

        public void Dispose()
        {
            Events.Add("dispose");
            DisposeCount++;
        }
    }
}
