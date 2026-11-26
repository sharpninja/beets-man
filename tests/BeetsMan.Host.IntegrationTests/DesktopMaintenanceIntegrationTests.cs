using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Runs the actual Toolkit commands across HTTPS and proves a batch can stage all three kinds without sending a confirmation.</summary>
    /// <remarks>2026-11-24: Bind TEST-BEETS-002 and TEST-LIBRARY-003/004 for FR-BEETS-001#ac-edit, ac-recover and FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-revert.</remarks>
    /// <remarks>2026-11-26: Release the stopped fixture's exclusive database before verifying its persisted state for TEST-BEETS-002.</remarks>
    [Fact]
    public async Task DesktopMaintenanceConfirmationReplaysOrStaysInTheLocalBatch()
    {
        const long revision = 42;
        var files = new[] { DesktopMaintenanceAssertions.Source, DesktopMaintenanceAssertions.Audio };
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        Directory.CreateDirectory(Path.Combine(music, "Inbox"));
        foreach (var path in files)
            await File.WriteAllTextAsync(Path.Combine(music, path), path, Token);
        var database = Path.Combine(root, "desktop-maintenance-host.db");
        var desktopDatabase = Path.Combine(root, "desktop-maintenance-local.db");
        var organizeTrack = Guid.NewGuid();
        var audioTrack = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = revision });
            var tracks = db.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(new BsonDocument { ["_id"] = organizeTrack, ["LibraryId"] = library, ["Revision"] = 40L,
                ["BeetsTrackId"] = 17L, ["Title"] = "Harbor Lights", ["RelativePath"] = files[0] });
            tracks.Insert(new BsonDocument { ["_id"] = audioTrack, ["LibraryId"] = library, ["Revision"] = 41L,
                ["BeetsTrackId"] = 18L, ["Title"] = "Night Crossing", ["RelativePath"] = files[1] });
        }

        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        builder.Services.AddSingleton<IBeetsMaintenancePlanner>(new ScriptedMaintenancePlan(DesktopMaintenanceAssertions.Destination));
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        await using var host = LibraryHostStartup.Build(builder, new(library, database, music, Path.Combine(root, "private"), Path.Combine(root, "work"), []),
            new Verifier(new(library, instance), secret, null), credentials, credentials);
        await host.StartAsync(Token);

        using var transport = new DesktopMaintenanceTransport(CreateClientHandler(certificate));
        using var client = new HttpClient(transport);
        var gateway = new HttpLibraryMaintenanceHost(client, new Uri(host.Urls.Single()), library, new TokenProvider(library, instance, secret));
        var request = new RequestContext(Guid.NewGuid(), instance);
        var scope = new LibraryBatchScope(library, instance);
        var selection = new CatalogTrackSelection([organizeTrack], revision);
        var catalogSelection = new CatalogTrackSelection([audioTrack, organizeTrack], revision);
        var audioSelection = new CatalogTrackSelection([audioTrack], revision);
        LibraryJobReceipt accepted;
        bool retainedPreview;
        bool wrongApprovalRejected;
        bool canConfirmOffline;
        bool localReceiptsOnly;
        LibraryBatchReview review;
        using (var local = LiteDbDesktopPlaylistStore.Open(desktopDatabase, allowCreate: true))
        {
            var dispatch = Routes(local);
            using var vm = new HostLibraryViewModel(new PresentationServices(dispatch, new InlineScheduler(), instance), Guid.NewGuid(), library)
                { Library = new(library, 1, true, true, true) };
            await vm.PreviewOrganizeCommand.ExecuteAsync(selection);
            var direct = Assert.IsType<OrganizePlan>(vm.OrganizePlan);
            await vm.ConfirmMovesCommand.ExecuteAsync(null);
            retainedPreview = vm.OrganizePlan == direct && vm.Failure is { Code: "library-maintenance-confirm-host-unreachable", CanRetry: true } &&
                vm.AcceptedJob is null;
            await vm.ConfirmMovesCommand.ExecuteAsync(null);
            accepted = Assert.IsType<LibraryJobReceipt>(vm.AcceptedJob);
            Assert.Null(vm.OrganizePlan);

            var mode = Success(await dispatch.QueryAsync(new GetLibraryBatchMode(), request, Token));
            mode = Success(await dispatch.SendAsync(new SetLibraryBatchMode(true, mode.Revision), Operation(), Token));
            await vm.ReceiveBatchModeAsync(mode);
            await vm.PreviewOrganizeCommand.ExecuteAsync(selection);
            await vm.PreviewRemovalCommand.ExecuteAsync(new RemovalSelection(catalogSelection, RemovalMode.CatalogOnly));
            var catalog = Assert.IsType<RemovalPlan>(vm.RemovalPlan);
            var changed = new RemovalApproval(catalog.PlanId, RemovalMode.CatalogAndAudio);
            await vm.ConfirmRemovalCommand.ExecuteAsync(changed);
            wrongApprovalRejected = !vm.ConfirmRemovalCommand.CanExecute(changed) && vm.RemovalPlan == catalog &&
                Success(await dispatch.QueryAsync(new GetLibraryBatchReview(), request, Token)).Entries.IsEmpty;
            var unapproved = new RemoveTracks(catalog.PlanId, revision, changed);
            Assert.IsType<Outcome<LibraryJobReceipt>.Invalid>(await dispatch.SendAsync(unapproved, Operation(), Token));

            vm.Library = vm.Library! with { IsOnline = false };
            canConfirmOffline = vm.ConfirmMovesCommand.CanExecute(null);
            await vm.ConfirmMovesCommand.ExecuteAsync(null);
            var first = Assert.IsType<LibraryBatchStageReceipt>(vm.StagedChange);
            await vm.ConfirmRemovalCommand.ExecuteAsync(new RemovalApproval(catalog.PlanId, catalog.Mode));
            var second = Assert.IsType<LibraryBatchStageReceipt>(vm.StagedChange);
            vm.Library = vm.Library! with { IsOnline = true };
            await vm.PreviewRemovalCommand.ExecuteAsync(new RemovalSelection(audioSelection, RemovalMode.CatalogAndAudio));
            var audio = Assert.IsType<RemovalPlan>(vm.RemovalPlan);
            var approveAudio = new RemovalApproval(audio.PlanId, audio.Mode);
            vm.Library = vm.Library! with { IsOnline = false };
            canConfirmOffline &= vm.ConfirmRemovalCommand.CanExecute(approveAudio);
            await vm.ConfirmRemovalCommand.ExecuteAsync(approveAudio);
            var third = Assert.IsType<LibraryBatchStageReceipt>(vm.StagedChange);
            review = Success(await dispatch.QueryAsync(new GetLibraryBatchReview(), request, Token));
            localReceiptsOnly = vm.AcceptedJob is null && vm.Job is null && vm.OrganizePlan is null && vm.RemovalPlan is null &&
                review.Entries.Select(entry => entry.OperationId).SequenceEqual(new[] { first.OperationId, second.OperationId, third.OperationId });
        }

        var moves = Assert.IsType<LibraryBatchChange.Organize>(review.Entries[0].Change).Plan;
        var catalogChange = Assert.IsType<LibraryBatchChange.Remove>(review.Entries[1].Change);
        var audioChange = Assert.IsType<LibraryBatchChange.Remove>(review.Entries[2].Change);
        var approvedCatalog = new RemoveTracks(catalogChange.Plan.PlanId, revision, catalogChange.Approval);
        var catalogOperation = new CommandContext(request, review.Entries[1].OperationId);
        bool replayed;
        string changedReplay;
        string lateReplay;
        int remaining;
        using (var reopened = LiteDbDesktopPlaylistStore.Open(desktopDatabase, allowCreate: false))
        {
            var dispatch = Routes(reopened);
            var repeated = Success(await dispatch.SendAsync(approvedCatalog, catalogOperation, Token));
            replayed = repeated.JobId == Guid.Empty && repeated.LocalBatch is { Position: 2 } replay && replay.OperationId == catalogOperation.OperationId;
            changedReplay = Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await dispatch.SendAsync(approvedCatalog with
            {
                Approval = new(approvedCatalog.PlanId, RemovalMode.CatalogAndAudio)
            }, catalogOperation, Token)).Code;
            Success(await dispatch.SendAsync(new RevertLibraryBatch(review.Version, KeepModeEnabled: false), Operation(), Token));
            remaining = Success(await dispatch.QueryAsync(new GetLibraryBatchMode(), request, Token)).ChangeCount;
            lateReplay = Assert.IsType<Outcome<LibraryJobReceipt>.Conflict>(await dispatch.SendAsync(approvedCatalog, catalogOperation, Token)).Code;
        }

        await host.StopAsync(Token);
        host.Services.GetRequiredService<LiteDbHostPlaylistStore>().Dispose();
        using var snapshot = new LiteDatabase(database);
        var jobs = snapshot.GetCollection<BsonDocument>("library_jobs").FindAll().ToArray();
        Assert.Equal(accepted.JobId, Assert.Single(jobs)["_id"].AsGuid);
        var intact = files.All(path => File.ReadAllText(Path.Combine(music, path)) == path) &&
            !File.Exists(Path.Combine(music, DesktopMaintenanceAssertions.Destination)) &&
            snapshot.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64 == revision &&
            snapshot.GetCollection<BsonDocument>("catalog_tracks").Count() == 2 &&
            snapshot.GetCollection<BsonDocument>("notification_deliveries").Count() == 0;
        var move = Assert.Single(moves.Moves);
        DesktopMaintenanceAssertions.Verify(new(transport.Attempts[0].OperationId, transport.Attempts[1].OperationId, accepted.JobId,
            retainedPreview, transport.Attempts.Count, jobs.Length, wrongApprovalRejected, canConfirmOffline,
            [$"{move.Source} -> {move.Destination}", $"{catalogChange.Plan.Mode}:{string.Join(',', catalogChange.Plan.AudioPaths)}",
                $"{audioChange.Plan.Mode}:{string.Join(',', audioChange.Plan.AudioPaths)}"],
            moves.TrackIds.SequenceEqual(selection.TrackIds) && move.TrackId == organizeTrack &&
                catalogChange.Plan.TrackIds.SequenceEqual(catalogSelection.TrackIds) && audioChange.Plan.TrackIds.SequenceEqual(audioSelection.TrackIds),
            localReceiptsOnly, replayed, changedReplay, remaining, lateReplay, intact));

        CommandContext Operation() => new(request, Guid.NewGuid());

        RequestDispatcher Routes(LiteDbDesktopPlaylistStore local)
        {
            var batch = new LibraryBatchHandlers(scope, local, new UnexpectedMaintenanceBatchPublisher());
            return new LibraryMaintenanceHandlers(scope, gateway, batch).Register(batch.Register(new())).Build();
        }

        static T Success<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;
    }

    private sealed class DesktopMaintenanceTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<ConfirmLibraryMaintenanceRequest> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/confirm", StringComparison.Ordinal) != true)
                return await base.SendAsync(request, token);
            Attempts.Add((await request.Content!.ReadFromJsonAsync<ConfirmLibraryMaintenanceRequest>(token))!);
            var result = await base.SendAsync(request, token);
            if (Attempts.Count != 1 || !result.IsSuccessStatusCode)
                return result;
            result.Dispose();
            throw new HttpRequestException("Lost the successful host response.");
        }
    }

    private sealed class UnexpectedMaintenanceBatchPublisher : IApprovedLibraryBatchPublisher
    {
        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview review, Guid operationId,
            CancellationToken token) => throw new InvalidOperationException("Reverting staged maintenance must not attempt publication.");
    }
}
