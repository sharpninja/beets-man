using System.Text.RegularExpressions;
using System.Threading.Channels;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.MediaSync;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Runs the proven presentation assertions against generated Toolkit command properties.</summary>
/// <remarks>2026-11-24: Bind TEST-DISPATCH-001 to production presentation code for FR-DISPATCH-001#ac-routing, ac-update, ac-responsive and ac-wait-pointer.</remarks>
public sealed class DISPATCHAcceptanceTests
{
    private readonly CancellationToken stop = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Routing_MatchesContract()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var device = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var existingTrack = Guid.NewGuid();
        var missingTrack = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var profile = "profile-1";
        var before = new DeviceExportPlan(plan, 8, device, profile, 4, []);
        var playlistEntry = new ExportEntry.Playlist(Guid.NewGuid(), "lists/mix.m3u8", playlist, "m3u8", [
            new(existingTrack, "media/existing.flac", ExportAudioStatus.Present),
            new(missingTrack, "media/missing.flac", ExportAudioStatus.Staged)
        ]);
        var after = before with { PlanRevision = 9, Entries = [playlistEntry,
            new ExportEntry.Audio(Guid.NewGuid(), "media/missing.flac", missingTrack, ExportAudioStatus.Staged)] };

        dispatcher.Read<GetDeviceExportPlan, DeviceExportPlan>(_ => Success(before));
        dispatcher.Write<StagePlaylistForDevice, DeviceExportPlan>(_ => Success(after));
        using var viewModel = new SyncListViewModel(new(dispatcher, scheduler, origin), Guid.NewGuid(), new(device, profile, 4));
        Task open = viewModel.OpenCommand.ExecuteAsync(plan);
        await scheduler.ApplyNextAsync(stop);
        await open;
        var opened = Assert.IsType<GetDeviceExportPlan>(Assert.Single(dispatcher.Reads).Request);
        Assert.Equal(new GetDeviceExportPlan(device, plan, profile, 4), opened);

        Task staging = viewModel.DropSavedPlaylistCommand.ExecuteAsync(playlist);
        var call = Assert.Single(dispatcher.Writes);
        var request = Assert.IsType<StagePlaylistForDevice>(call.Request);
        PresentationAssertions.VerifyDispatch(new("StagePlaylistForDevice", 1, "device-1", profile, 4, 8),
            new(request.GetType().Name, dispatcher.Writes.Count, request.DeviceId == device ? "device-1" : "unknown", request.ProfileId,
                request.ProfileRevision, request.ExpectedPlanRevision));
        Assert.Equal(plan, request.PlanId);
        Assert.Equal(playlist, request.PlaylistId);
        Assert.NotEqual(Guid.Empty, call.OperationId);
        Assert.Equal(origin, call.Context.OriginInstanceId);
        Assert.Same(before, viewModel.Plan);
        await scheduler.ApplyNextAsync(stop);
        await staging;
        Assert.Same(after, viewModel.Plan);
        Assert.Equal([existingTrack, missingTrack], playlistEntry.Tracks.Select(track => track.TrackId));
        Assert.Equal(missingTrack, Assert.Single(after.Entries.OfType<ExportEntry.Audio>()).TrackId);
    }

    [Fact]
    public async Task Update_MatchesContract()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var oldResponse = Reply<CatalogPage>();
        var latestResponse = Reply<CatalogPage>();
        dispatcher.Read<BrowseCatalog, CatalogPage>(request => request.Filter.Text == "first" ? oldResponse.Task : latestResponse.Task);
        using var viewModel = new LibraryViewModel(new(dispatcher, scheduler), Guid.NewGuid(), new(Guid.NewGuid()));

        Task old = viewModel.SearchCommand.ExecuteAsync("first");
        Task latest = viewModel.SearchCommand.ExecuteAsync("second");
        Assert.Equal(2, dispatcher.Reads.Count);
        Assert.True(dispatcher.Reads[0].Token.IsCancellationRequested);
        Assert.NotEqual(dispatcher.Reads[0].Context.CorrelationId, dispatcher.Reads[1].Context.CorrelationId);
        latestResponse.SetResult(new Outcome<CatalogPage>.Success(Catalog("second", 2)));
        var before = viewModel.Page?.Filter.Text;
        await scheduler.ApplyNextAsync(stop);
        await latest;
        var after = viewModel.Page?.Filter.Text;
        oldResponse.SetResult(new Outcome<CatalogPage>.Success(Catalog("first", 3)));
        await scheduler.ApplyNextAsync(stop);
        await old;
        PresentationAssertions.VerifyLatestSearch(new(before, after, viewModel.Page?.Filter.Text));
    }

    /// <summary>Rejects a read overtaken by an offline write at the same host revision.</summary>
    /// <remarks>2026-11-25: Preserve FR-RECORDS-001#ac-offline and FR-DISPATCH-001#ac-update.</remarks>
    [Fact]
    public async Task LateFavoriteReadCannotReplacePendingEditAtTheSameHostRevision()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var late = Reply<FavoriteState>();
        var track = Guid.NewGuid();
        dispatcher.Read<GetFavorite, FavoriteState>(_ => late.Task);
        dispatcher.Write<SetFavorite, FavoriteState>(_ => Success(new FavoriteState(track, true, 4, true)));
        using var viewModel = new LibraryViewModel(new(dispatcher, scheduler), Guid.NewGuid(), new(Guid.NewGuid()));
        var read = viewModel.InspectFavoriteCommand.ExecuteAsync(track);
        var write = viewModel.SetFavoriteCommand.ExecuteAsync(new FavoriteEdit(track, true, 4));
        await scheduler.ApplyNextAsync(stop);
        await write;
        late.SetResult(new Outcome<FavoriteState>.Success(new(track, false, 4, false)));
        await scheduler.ApplyNextAsync(stop);
        await read;
        Assert.Equal(new FavoriteState(track, true, 4, true), viewModel.Favorites[track]);
    }

    [Fact]
    public async Task Responsive_MatchesContract()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var services = new PresentationServices(dispatcher, scheduler);
        var libraryId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var reply = Reply<LibraryJobReceipt>();
        dispatcher.Read<GetHostLibraryState, HostLibraryState>(_ => Success(new HostLibraryState(libraryId, 3, true, true, true)));
        dispatcher.Write<UploadImportSources, LibraryJobReceipt>(_ => reply.Task);
        dispatcher.Write<NavigateWorkspace, ShellState>(request => Success(new ShellState(windowId, 1, request.Route,
            PresentationMode.Normal, PresentationMode.Normal, Guid.NewGuid(), [])));

        using var shell = new ShellViewModel(services, windowId);
        using var host = new HostLibraryViewModel(services, Guid.NewGuid(), libraryId);
        Task open = host.OpenLibraryCommand.ExecuteAsync(null);
        await scheduler.ApplyNextAsync(stop);
        await open;
        host.SourceSelection = new(Guid.NewGuid(), ["album.flac"]);
        Task import = host.UploadCopiesCommand.ExecuteAsync(null);
        Assert.False(host.IsInteractionBlocked);
        await host.PickSourcesCommand.ExecuteAsync(null);

        Task navigate = shell.NavigateCommand.ExecuteAsync(new WorkspaceRoute(Workspace.Library));
        await scheduler.ApplyNextAsync(stop);
        await navigate;
        PresentationAssertions.VerifyResponsive(new(dispatcher.Writes.Count(call => call.Request is not NavigateWorkspace),
            shell.State?.Route.Workspace.ToString(), import.IsCompleted));
        var accepted = new LibraryJobReceipt(Guid.NewGuid(), libraryId, 1);
        reply.SetResult(new Outcome<LibraryJobReceipt>.Success(accepted));
        await scheduler.ApplyNextAsync(stop);
        await import;
        Assert.Equal(accepted, host.AcceptedJob);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task WaitPointer_MatchesContract(string outcome)
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var reply = Reply<BootstrapState>();
        dispatcher.Write<RetryDesktopBootstrap, BootstrapState>(_ => reply.Task);
        using var viewModel = new BootstrapViewModel(new(dispatcher, scheduler), Guid.NewGuid());
        Task operation = viewModel.RetryCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsInteractionBlocked);
        switch (outcome)
        {
            case "success": reply.SetResult(new Outcome<BootstrapState>.Success(new(1, BootstrapStatus.Ready, null, null))); break;
            case "failure": reply.SetResult(new Outcome<BootstrapState>.DependencyFailure("database-unavailable", true)); break;
            default:
                viewModel.RetryCommand.Cancel();
                reply.SetCanceled(dispatcher.Writes[0].Token);
                break;
        }
        await scheduler.ApplyNextAsync(stop);
        await operation;
        Assert.False(viewModel.IsInteractionBlocked);
        Assert.False(viewModel.IsWriting);
        Assert.Equal(outcome == "failure" ? "database-unavailable" : null, viewModel.Failure?.Code);
    }

    [Fact]
    public async Task ScopeReplacement_DropsOutstandingProjection()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var reply = Reply<BootstrapState>();
        dispatcher.Write<RetryDesktopBootstrap, BootstrapState>(_ => reply.Task);
        using var viewModel = new BootstrapViewModel(new(dispatcher, scheduler), Guid.NewGuid());
        Task operation = viewModel.RetryCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsInteractionBlocked);
        viewModel.Activate(Guid.NewGuid());
        Assert.False(viewModel.IsInteractionBlocked);
        reply.SetResult(new Outcome<BootstrapState>.Success(new(2, BootstrapStatus.Ready, null, null)));
        await scheduler.ApplyNextAsync(stop);
        await operation;
        Assert.Null(viewModel.State);
    }

    [Fact]
    public async Task Retry_ReusesOperationIdentityAndKeepsTheInvalidDraft()
    {
        var dispatcher = new RecordingDispatcher();
        var scheduler = new QueuedScheduler();
        var services = new PresentationServices(dispatcher, scheduler);
        var draft = new PlaylistDraft(Guid.NewGuid(), 5, "Draft", [], null);
        dispatcher.Read<GetPlaylistDraft, PlaylistDraft>(_ => Success(draft));
        dispatcher.Write<SavePlaylist, PlaylistSavedReceipt>(_ => Task.FromResult<Outcome<PlaylistSavedReceipt>>(
            new Outcome<PlaylistSavedReceipt>.Invalid([new("name-required", "Name")])));
        using var editor = new PlaylistDraftViewModel(services, Guid.NewGuid(), new(Guid.NewGuid()));
        using var shell = new ShellViewModel(services, Guid.NewGuid());
        using var error = new ActionErrorViewModel(services, shell);
        Task open = editor.OpenCommand.ExecuteAsync(draft.DraftId);
        await scheduler.ApplyNextAsync(stop);
        await open;
        editor.Name = "";
        Task save = editor.SaveCommand.ExecuteAsync(null);
        await scheduler.ApplyNextAsync(stop);
        await save;
        Assert.Equal(PresentationFailureKind.Invalid, editor.Failure?.Kind);
        Assert.Equal("Name", Assert.Single(editor.Failure!.Issues).Field);
        error.Show(Guid.NewGuid(), editor.Failure);
        Task retry = error.RetryCommand.ExecuteAsync(null);
        await scheduler.ApplyNextAsync(stop);
        await retry;

        Assert.Equal(2, dispatcher.Writes.Count);
        Assert.Equal(dispatcher.Writes[0].OperationId, dispatcher.Writes[1].OperationId);
        Assert.NotEqual(dispatcher.Writes[0].Context.CorrelationId, dispatcher.Writes[1].Context.CorrelationId);
        Assert.Equal(dispatcher.Writes[0].Request, dispatcher.Writes[1].Request);
        Assert.Equal("", editor.Name);
        Assert.Same(draft, editor.Draft);
        Assert.Null(editor.Saved);
    }

    /// <summary>Matches every Toolkit command property to its documented binding and validates declared CQRS results.</summary>
    /// <remarks>2026-11-25: Include the panel routes and visualization result for FR-PLUGINS-001#ac-visualize and FR-DISPATCH-001#ac-routing.</remarks>
    /// <remarks>2026-11-25: Require complete coverage of synchronous and asynchronous bindings for FR-DISPATCH-001#ac-routing.</remarks>
    [Fact]
    public void CommandCatalog_MatchesToolkitBindings()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "ui-commands.md");
        var catalog = File.ReadAllText(file);
        var owners = typeof(PresentationViewModel).Assembly.GetTypes()
            .Where(type => !type.IsNested && typeof(PresentationViewModel).IsAssignableFrom(type))
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        var contracts = typeof(IRequestDispatcher).Assembly.GetExportedTypes().Where(type => !type.IsNested)
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        const string routePattern = @"\b(?<kind>[CQ]) `(?<request>\w+)\([^`]*\)` → `(?<result>\w+)`";
        foreach (Match row in Regex.Matches(catalog, @"(?m)^- \*\*(?<owner>\w+)\.(?<names>.*?)\*\* · (?<mapping>.*)$"))
        {
            string owner = row.Groups["owner"].Value + "ViewModel";
            Assert.True(owners.TryGetValue(owner, out var type), $"Missing ViewModel {owner}.");
            var text = row.Groups["mapping"].Value;
            var local = text.StartsWith("Local. ", StringComparison.Ordinal);
            var replay = text.StartsWith("Replay. ", StringComparison.Ordinal);
            var names = Regex.Matches(row.Groups["names"].Value, @"`(?<name>\w+)`");
            Assert.NotEmpty(names);
            foreach (Match name in names)
            {
                var propertyName = name.Groups["name"].Value + "Command";
                var property = type.GetProperty(propertyName);
                Assert.True(property is not null, $"Missing {owner}.{propertyName}.");
                Assert.True(typeof(IRelayCommand).IsAssignableFrom(property.PropertyType), $"{owner}.{propertyName} is not a Toolkit command.");
                Assert.True(typeof(IAsyncRelayCommand).IsAssignableFrom(property.PropertyType) != local,
                    $"{owner}.{propertyName} has the wrong local or asynchronous binding kind.");
                Assert.True(bindings.Add($"{owner}.{propertyName}"));
            }
            var mappings = Regex.Matches(text, routePattern);
            if (replay)
            {
                Assert.Equal("ActionErrorViewModel", owner);
                Assert.Equal("Retry", Assert.Single(names).Groups["name"].Value);
                Assert.Empty(mappings);
                continue;
            }

            if (!local)
            {
                Assert.NotEmpty(mappings);
                Assert.Equal(0, mappings[0].Index);
            }
            Assert.Equal(local ? 0 : text.Count(character => character == '→'), mappings.Count);
            foreach (Match mapping in mappings)
            {
                var request = mapping.Groups["request"].Value;
                var result = mapping.Groups["result"].Value;
                Assert.True(contracts.TryGetValue(request, out var requestType), $"Missing request contract {request}.");
                Assert.True(contracts.TryGetValue(result, out var resultType), $"Missing result contract {result}.");
                var definition = mapping.Groups["kind"].Value == "C" ? typeof(ICommand<>) : typeof(IQuery<>);
                Assert.True(definition.MakeGenericType(resultType).IsAssignableFrom(requestType), $"Incorrect result type for {request}.");
            }
        }
        var generated = owners.Values.SelectMany(type => type.GetProperties()
            .Where(property => typeof(IRelayCommand).IsAssignableFrom(property.PropertyType))
            .Select(property => $"{type.Name}.{property.Name}"));
        Assert.Equal(generated.Order(StringComparer.Ordinal), bindings.Order(StringComparer.Ordinal));
    }

    private static CatalogPage Catalog(string term, long revision) => new(CatalogCategory.Search, new CatalogFilter(Text: term),
        new CatalogSort(CatalogSortKey.TrackNumber), revision, []);

    private static TaskCompletionSource<Outcome<T>> Reply<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<Outcome<T>> Success<T>(T value) => Task.FromResult<Outcome<T>>(new Outcome<T>.Success(value));

    private sealed class QueuedScheduler : IUiScheduler
    {
        private readonly Channel<(Action Apply, TaskCompletionSource Done)> callbacks = Channel.CreateUnbounded<(Action, TaskCompletionSource)>();

        public Task PostAsync(Action apply, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(callbacks.Writer.TryWrite((apply, done)));
            return done.Task;
        }

        public async Task ApplyNextAsync(CancellationToken token)
        {
            var (apply, done) = await callbacks.Reader.ReadAsync(token);
            try { apply(); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); throw; }
        }
    }

    private sealed record Call(object Request, RequestContext Context, Guid? OperationId, CancellationToken Token);

    private sealed class RecordingDispatcher : IRequestDispatcher
    {
        private readonly Dictionary<Type, Delegate> handlers = [];
        public List<Call> Reads { get; } = [];
        public List<Call> Writes { get; } = [];

        public void Read<TRequest, TResult>(Func<TRequest, Task<Outcome<TResult>>> handler) where TRequest : IQuery<TResult> =>
            handlers.Add(typeof(TRequest), (Func<object, Task<Outcome<TResult>>>)(request => handler((TRequest)request)));

        public void Write<TRequest, TResult>(Func<TRequest, Task<Outcome<TResult>>> handler) where TRequest : ICommand<TResult> =>
            handlers.Add(typeof(TRequest), (Func<object, Task<Outcome<TResult>>>)(request => handler((TRequest)request)));

        public Task<Outcome<TResult>> SendAsync<TResult>(ICommand<TResult> command, CommandContext context, CancellationToken token)
        {
            Writes.Add(new(command, context.Request, context.OperationId, token));
            return Invoke<TResult>(command);
        }

        public Task<Outcome<TResult>> QueryAsync<TResult>(IQuery<TResult> query, RequestContext context, CancellationToken token)
        {
            Reads.Add(new(query, context, null, token));
            return Invoke<TResult>(query);
        }

        private Task<Outcome<TResult>> Invoke<TResult>(object request) =>
            ((Func<object, Task<Outcome<TResult>>>)handlers[request.GetType()])(request);
    }
}
