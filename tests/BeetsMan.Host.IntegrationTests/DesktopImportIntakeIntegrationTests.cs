using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Runs the desktop's import commands across the actual HTTPS intake and recovers a lost acceptance reply.</summary>
    /// <remarks>2026-11-24: Bind FR-BEETS-001#ac-import, ac-recover and FR-DISPATCH-001#ac-responsive to the desktop and host.</remarks>
    [Fact]
    public async Task DesktopUploadRetainsTheSelectionAndRecoversTheAcceptedJobWithoutRecopying()
    {
        var database = Path.Combine(root, "desktop-import-host.db");
        SeedHost(database);
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(root, "desktop")).FullName;
        var first = Path.Combine(local, "Selected A.flac");
        var second = Path.Combine(local, "Selected B.flac");
        var changed = Path.Combine(local, "Changing.flac");
        await File.WriteAllTextAsync(first, "fixture A", Token);
        await File.WriteAllTextAsync(second, "fixture B", Token);
        await File.WriteAllTextAsync(changed, "revision 1", Token);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var host = LibraryHostStartup.Build(builder, new(library, database, music, Path.Combine(root, "private"), Path.Combine(root, "work"), []),
            new Verifier(new(library, instance), secret, null), credentials, credentials);
        await host.StartAsync(Token);

        var address = new Uri(host.Urls.Single());
        using var wire = new ImportIntakeTransport(CreateClientHandler(certificate));
        using var http = new HttpClient(wire);
        var picker = new ImportFilePicker { Files = [first, second] };
        var gateway = new HttpImportIntakeHost(http, address, library, new TokenProvider(library, instance, secret));
        var window = Guid.NewGuid();
        var shellState = new ShellState(window, 1, new(Workspace.HostLibrary), PresentationMode.Normal, PresentationMode.Normal, Guid.NewGuid(), []);
        var routes = new RequestDispatcher.Builder().RegisterQuery<GetHostLibraryState, HostLibraryState>(new AvailableImportLibrary(library));
        new ShellHandlers(shellState, new NoImportAndroidConnections()).Register(routes);
        var dispatcher = new ImportIntakeHandlers(library, picker, gateway).Register(routes).Build();
        var services = new PresentationServices(dispatcher, new InlineScheduler(), instance);
        using var shell = new ShellViewModel(services, window);
        using var viewModel = new HostLibraryViewModel(services, Guid.NewGuid(), library);
        viewModel.Activate(viewModel.Scope.OwnerId);
        await viewModel.OpenLibraryCommand.ExecuteAsync(null);
        await viewModel.PickSourcesCommand.ExecuteAsync(null);
        var selection = Assert.IsType<ImportSourceSelection>(viewModel.SourceSelection);
        Assert.Equal(viewModel.Scope.OwnerId, picker.Owner);
        picker.Files = [];
        await viewModel.PickSourcesCommand.ExecuteAsync(null);
        var cancelled = viewModel.SourceSelection == selection && wire.Calls.IsEmpty;
        var other = await dispatcher.SendAsync(new UploadImportSources(library, selection.SelectionToken), new(new(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()), Token);
        var foreign = other is Outcome<LibraryJobReceipt>.Invalid && wire.Calls.IsEmpty;

        var upload = viewModel.UploadCopiesCommand.ExecuteAsync(null);
        bool responsive;
        try
        {
            await wire.FirstAcceptance.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await shell.NavigateCommand.ExecuteAsync(new WorkspaceRoute(Workspace.Library));
            responsive = !upload.IsCompleted && !viewModel.IsInteractionBlocked && shell.State?.Route.Workspace == Workspace.Library;
        }
        finally { wire.DropReply.TrySetResult(); }
        await upload;
        var retained = viewModel.SourceSelection == selection && viewModel.AcceptedJob is null &&
            viewModel.Failure is { Code: "import-start-host-unreachable", CanRetry: true };

        // The host already has complete copies. The retry must work even if the desktop cannot reopen a source.
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await viewModel.UploadCopiesCommand.ExecuteAsync(null);
        var accepted = Assert.IsType<LibraryJobReceipt>(viewModel.AcceptedJob);
        var released = viewModel.SourceSelection is null && viewModel.Failure is null;
        await viewModel.InspectJobCommand.ExecuteAsync(accepted.JobId);
        var inspected = Assert.IsType<LibraryJobStatus>(viewModel.Job);
        var calls = wire.Calls.ToArray();
        var starts = wire.Starts.ToArray();
        var manifest = Assert.Single(wire.Manifests);
        Assert.Equal(selection.Names, manifest.Files.Select(file => file.Name).ToImmutableArray());
        var sameOperation = starts.Length == 2 && starts[0] == starts[1] && starts[0].OperationId == manifest.UploadId &&
            starts[0].UploadId == manifest.UploadId && starts[0].OriginInstanceId == instance;

        picker.Files = [changed];
        await viewModel.PickSourcesCommand.ExecuteAsync(null);
        wire.AfterManifest = () => File.WriteAllText(changed, "revision 2");
        await viewModel.UploadCopiesCommand.ExecuteAsync(null);
        var changedRejected = viewModel.Failure is { Kind: PresentationFailureKind.Invalid } failure &&
            failure.Issues.Any(issue => issue.Code == "import-copy-mismatch") &&
            wire.Calls.ToArray().Skip(calls.Length).SequenceEqual(["manifest", "part:0"]);

        DesktopImportAssertions.Verify(new([.. selection.Names], calls, cancelled, foreign, retained, released, sameOperation,
            changedRejected, await File.ReadAllTextAsync(first, Token) == "fixture A" && await File.ReadAllTextAsync(second, Token) == "fixture B",
            accepted.JobId, inspected.JobId, inspected.Phase.ToString(), responsive));
        await host.DisposeAsync();
        using var db = new LiteDatabase(database);
        Assert.Equal(1, db.GetCollection<BsonDocument>("library_jobs").Count());
    }

    private sealed class AvailableImportLibrary(Guid libraryId) : IQueryHandler<GetHostLibraryState, HostLibraryState>
    {
        public Task<Outcome<HostLibraryState>> HandleAsync(GetHostLibraryState query, RequestContext context, CancellationToken token) =>
            Task.FromResult<Outcome<HostLibraryState>>(new Outcome<HostLibraryState>.Success(new(libraryId, 1, true, true, true)));
    }

    private sealed class NoImportAndroidConnections : IAndroidConnectionRegistry
    {
        public bool IsLiveRegisteredConnection(Guid peerId, long generation) => false;
    }

    private sealed class ImportFilePicker : IImportSourcePicker
    {
        public Guid Owner { get; private set; }
        public ImmutableArray<string> Files { get; set; }

        public Task<ImmutableArray<string>> PickFilesAsync(Guid ownerId, CancellationToken token)
        {
            Owner = ownerId;
            return Task.FromResult(Files);
        }
    }

    /// <summary>Observes the real upload and discards its first committed acknowledgement.</summary>
    private sealed class ImportIntakeTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource FirstAcceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DropReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<BeginImportUploadRequest> Manifests { get; } = new();
        public ConcurrentQueue<StartImportRequest> Starts { get; } = new();
        public Action? AfterManifest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/import-uploads", StringComparison.Ordinal))
            {
                Calls.Enqueue("manifest");
                Manifests.Enqueue((await request.Content!.ReadFromJsonAsync<BeginImportUploadRequest>(cancellationToken))!);
                var response = await base.SendAsync(request, cancellationToken);
                var callback = AfterManifest;
                AfterManifest = null;
                callback?.Invoke();
                return response;
            }
            if (request.Method == HttpMethod.Put)
                Calls.Enqueue($"part:{path[(path.LastIndexOf('/') + 1)..]}");
            else if (path.EndsWith("/library-jobs/import", StringComparison.Ordinal))
            {
                Calls.Enqueue("start");
                Starts.Enqueue((await request.Content!.ReadFromJsonAsync<StartImportRequest>(cancellationToken))!);
                var response = await base.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode && FirstAcceptance.TrySetResult())
                {
                    using (response)
                        await DropReply.Task.WaitAsync(cancellationToken);
                    throw new HttpRequestException("The fixture discarded the committed import receipt.");
                }
                return response;
            }
            else
                Calls.Enqueue("job");
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
