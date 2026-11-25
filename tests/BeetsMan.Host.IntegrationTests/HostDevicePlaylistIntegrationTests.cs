using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Checks device playlist staging against an actual host database and private content store.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-001 to FR-FILESYNC-001#ac-playlist-drop and ac-safety using an independent device inventory.</remarks>
public sealed class HostDevicePlaylistIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beets-device-stage-{Guid.NewGuid():N}");
    private readonly Guid library = Guid.NewGuid();
    private readonly Guid origin = Guid.NewGuid();
    private readonly Guid device = Guid.NewGuid();
    private readonly Guid trackA = Guid.NewGuid();
    private readonly Guid trackB = Guid.NewGuid();
    private string DatabasePath => Path.Combine(root, "host.db");
    private string PrivateStage => Path.Combine(root, "staged");
    private string ActiveShare => Path.Combine(root, "share");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private AuthenticatedHostInstance Caller => new(library, origin);
    private RegisteredAndroidExport Receiver => new(library, device, 1, "owned-android-share", "fixture-receiver", new("https://receiver.test/"), false);

    /// <summary>Verifies partial arrival and a propagating removal against the actual published files and durable transfer.</summary>
    /// <remarks>2026-11-24: Bind TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety to stable sender and receiver index evidence.</remarks>
    [Fact]
    public async Task ReceiverProgressRequiresTheExpectedFileVersionsAndTheRemoval()
    {
        Seed();
        var source = Path.Combine(root, "source.flac");
        await File.WriteAllTextAsync(source, "data", Token);
        var workRoot = Directory.CreateDirectory(Path.Combine(root, "publication-work")).FullName;
        var payloads = new FileStagedPlaylistContent(PrivateStage);
        string sourceDigest;
        await using (var input = File.OpenRead(source))
            sourceDigest = await payloads.StoreAsync(input, Token);
        var readyDigest = await payloads.StoreAsync([.. System.Text.Encoding.UTF8.GetBytes("ready")], Token);
        var retiredDigest = await payloads.StoreAsync([.. System.Text.Encoding.UTF8.GetBytes("retired")], Token);
        using var index = new TransferIndexApi(Receiver, ActiveShare);
        using var transport = new HttpClient(index, disposeHandler: false);
        var sender = new SyncthingHostExportScanner(transport, new SenderRegistry(Receiver, ActiveShare));
        var verifier = new SyncthingDeviceExportReceiverVerifier(transport, sender, new FixtureTokenProvider(Receiver));
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        CompletedDeviceExportPublication transfer;
        DeviceExportScanCheckpoint first, pendingRemoval, unstable, completed;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var saved = await CreatePlaylistAsync(store, "Ready");
            var stage = CreateStager(store, new FixtureResolver(Receiver, trackA, trackB));
            var plan = Value(await stage.StageAsync(library, Caller, new(origin, Guid.NewGuid(), saved.PlaylistId,
                device, "musicolet", 2, null, null), Token)).Plan;
            var snapshot = new FileDeviceExportSnapshotReader(ActiveShare, Receiver);
            var publisher = new HostDeviceExportPublisher(store, store, new FileDeviceExportStepWriter(ActiveShare, workRoot, Receiver, payloads));
            await PublishAsync(DeviceExportPublicationMode.AddToShare,
                [new(new(ExportContentKind.Playlist, Guid.NewGuid()), "Playlists/Retired.m3u8", true, true, retiredDigest)]);
            transfer = await PublishAsync(DeviceExportPublicationMode.ReconcileLayout,
                [new(new(ExportContentKind.Audio, trackA), "Music/new.flac", true, true, sourceDigest),
                    new(new(ExportContentKind.Playlist, saved.PlaylistId), "Playlists/Ready.m3u8", true, true, readyDigest)]);
            var scan = new DeviceExportScanDispatcher(store, sender, clock);
            Assert.True(Value(await scan.RunNextAsync(Token)));
            first = await ObserveAsync();

            index.ReceiverHasCurrentPlaylist = true;
            pendingRemoval = await ObserveAsync();
            index.ReceiverHasTombstone = true;
            index.UnstableReceiver = true;
            unstable = await ObserveAsync();

            async Task<CompletedDeviceExportPublication> PublishAsync(DeviceExportPublicationMode mode, ImmutableArray<DeviceExportFileCandidate> files)
            {
                var current = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
                var share = Value(await snapshot.ReadAsync(Receiver, files.Select(file => file.Path).Concat(current.Files.Select(file => file.Path)), Token));
                var preview = Value(new DeviceExportPublicationPlanner().Prepare(plan, Receiver, current, share, mode, files));
                var proposal = preview.RemovalProposal;
                var intent = new DeviceExportPublicationIntent(Guid.NewGuid(), plan.PlanId!.Value, plan.PlanRevision!.Value,
                    proposal is null ? null : new(proposal.ProposalId, proposal.PlanId, proposal.PlanRevision));
                Assert.IsType<DeviceExportPublicationReservation.Pending>(Value(await store.ReservePublicationAsync(library, Caller, intent, preview, Token)));
                return Value(await publisher.ResumeAsync(library, Caller, intent, Token));
            }

            async Task<DeviceExportScanCheckpoint> ObserveAsync()
            {
                var dispatch = new DeviceExportReceiverDispatcher(store, verifier, clock);
                clock.Advance(TimeSpan.FromSeconds(11));
                Assert.True(Value(await dispatch.RunNextAsync(Token)));
                return Value(await store.ReadScanAsync(library, Caller, transfer.TransferId!.Value, Token));
            }
        }

        index.UnstableReceiver = false;
        using (var reopened = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            var dispatch = new DeviceExportReceiverDispatcher(reopened, verifier, clock);
            Assert.True(Value(await dispatch.RunNextAsync(Token)));
            completed = Value(await reopened.ReadScanAsync(library, Caller, transfer.TransferId!.Value, Token));
            Assert.False(Value(await dispatch.RunNextAsync(Token)));
            Assert.Equal(3, index.SenderReadPaths.Count);
        }

        var actualFile = Path.Combine(ActiveShare, "Music", "new.flac");
        await File.WriteAllTextAsync(actualFile, "tampered", Token);
        var changed = await verifier.CaptureSenderAsync(Receiver, [new("Music/new.flac", sourceDigest)], Token);
        using var final = LiteDbHostPlaylistStore.Open(DatabasePath, false);
        var retained = Value(await final.ReadScanAsync(library, Caller, transfer.TransferId!.Value, Token));
        StageAssertions.VerifyExportArrival(new([.. index.SenderReadPaths], first.BytesVerified, first.TotalBytes!.Value,
            first.State == DeviceExportScanState.AwaitingReceiver, pendingRemoval.State == DeviceExportScanState.AwaitingReceiver && pendingRemoval.BytesVerified == 9,
            unstable.State == DeviceExportScanState.AwaitingReceiver && unstable.FailureCode == "export-index-changed",
            completed.State == DeviceExportScanState.Completed, completed.BytesVerified, retained == completed,
            await File.ReadAllTextAsync(source, Token) == "data", changed is HostOutcome<ImmutableArray<DeviceExportTransferItem>>.Rejected
                { Code: "export-sender-file-changed" }));
    }

    /// <summary>Recovers one scan after a failed sender request and an abandoned lease across store restarts.</summary>
    /// <remarks>2026-11-24: Bind durable scan and exact Syncthing folder checks for TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public async Task CommittedPublicationRecoversItsSenderScanAcrossRestarts()
    {
        var receiver = Receiver with { ShareId = "android #1" };
        Seed(receiver);
        SeedTracks();
        var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "A.flac"), "published audio", Token);
        var workRoot = Directory.CreateDirectory(Path.Combine(root, "publication-work")).FullName;
        var payloads = new FileStagedPlaylistContent(PrivateStage);
        using var server = new SenderScanApi(receiver, ActiveShare);
        using var client = new HttpClient(server, disposeHandler: false);
        var scanner = new SyncthingHostExportScanner(client, new SenderRegistry(receiver, ActiveShare));
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        bool noEarlyDispatch;
        DeviceExportPublicationIntent publish;
        CompletedDeviceExportPublication receipt;
        DeviceExportScanCheckpoint queued;
        DeviceExportScanCheckpoint failed;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var stager = CreateStager(store, new FixtureResolver(receiver, trackA, trackB, "Library", "Lists/Ready.m3u8"));
            var saved = await CreatePlaylistAsync(store, "Ready");
            var stage = Value(await stager.StageAsync(library, Caller,
                new(origin, Guid.NewGuid(), saved.PlaylistId, device, "musicolet", 2, null, null), Token)).Plan;
            var publisher = new HostDeviceExportPublisher(store, store, new FileDeviceExportStepWriter(ActiveShare, workRoot, receiver, payloads));
            var workflow = new StagedDeviceExportWorkflow(store, store, store, new FileDeviceExportSnapshotReader(ActiveShare, receiver), publisher, payloads, media);
            var preview = Value(await workflow.PrepareAsync(library, Caller,
                new(Guid.NewGuid(), stage.PlanId!.Value, stage.PlanRevision!.Value, device, "musicolet", 2), Token));
            publish = new(Guid.NewGuid(), stage.PlanId.Value, preview.Plan.PlanRevision!.Value);
            Assert.IsType<DeviceExportPublicationReservation.Pending>(Value(await store.ReservePublicationAsync(library, Caller, publish, preview, Token)));
            var dispatcher = new DeviceExportScanDispatcher(store, scanner, clock);
            noEarlyDispatch = !Value(await dispatcher.RunNextAsync(Token)) && server.ScannedFolders.Count == 0;
            receipt = Value(await publisher.ResumeAsync(library, Caller, publish, Token));
            queued = Value(await store.ReadScanAsync(library, Caller, receipt.TransferId!.Value, Token));
            Assert.Equal("published audio", await File.ReadAllTextAsync(Path.Combine(ActiveShare, "Library", "A.flac"), Token));
            Assert.True(Value(await dispatcher.RunNextAsync(Token)));
            failed = Value(await store.ReadScanAsync(library, Caller, receipt.TransferId.Value, Token));
            Assert.Equal(DeviceExportScanState.Queued, failed.State);
            Assert.Equal(1, failed.Attempts);
        }

        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var dispatcher = new DeviceExportScanDispatcher(store, scanner, clock);
            Assert.False(Value(await dispatcher.RunNextAsync(Token)));
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.NotNull(Value(await store.ClaimScanAsync(clock.GetUtcNow(), Token)));
        }

        using var reopened = LiteDbHostPlaylistStore.Open(DatabasePath, false);
        var retry = new DeviceExportScanDispatcher(reopened, scanner, clock);
        Assert.False(Value(await retry.RunNextAsync(Token)));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(Value(await retry.RunNextAsync(Token)));
        var accepted = Value(await reopened.ReadScanAsync(library, Caller, receipt.TransferId!.Value, Token));
        var replay = Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await reopened.FindPublicationAsync(library, Caller, publish, Token)));
        var noFurtherDispatch = !Value(await retry.RunNextAsync(Token));

        server.Identity = "unexpected-sender";
        var wrongSender = await scanner.ScanAsync(receiver, Token);
        server.Identity = SenderRegistry.SenderId;
        server.IncludeAnotherPeer = true;
        var wrongFolder = await scanner.ScanAsync(receiver, Token);
        StageAssertions.VerifyExportScan(new(noEarlyDispatch, receipt.PublicationId, receipt.TransferId, queued.State.ToString(), failed.FailureCode!,
            accepted.Attempts, accepted.State.ToString(), [.. server.ScannedFolders],
            wrongSender is HostOutcome<bool>.Rejected { Code: "export-scan-device-mismatch" },
            wrongFolder is HostOutcome<bool>.Rejected { Code: "export-scan-folder-scope-mismatch" }, receipt == replay.Receipt, noFurtherDispatch));
    }

    /// <summary>Revises a colliding same-profile layout, protecting previous files until the exact removal is approved.</summary>
    /// <remarks>2026-11-24: Bind all STB20 correction assertions to FR-FILESYNC-001#ac-safety and TEST-FILESYNC-001/002.</remarks>
    [Fact]
    public async Task LayoutCorrectionKeepsThePriorExportUntilItsExactRemovalIsApproved()
    {
        Seed();
        SeedTracks();
        var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var refreshed = Path.Combine(media, "A.flac");
        await File.WriteAllTextAsync(refreshed, "old audio", Token);
        await File.WriteAllTextAsync(Path.Combine(media, "B.flac"), "moving audio", Token);
        var work = Directory.CreateDirectory(Path.Combine(root, "publication-work")).FullName;
        var foreign = Path.Combine(Directory.CreateDirectory(Path.Combine(ActiveShare, "Unsafe")).FullName, "Foreign.flac");
        await File.WriteAllTextAsync(foreign, "outside ownership", Token);
        var payloads = new FileStagedPlaylistContent(PrivateStage);
        var format = new M3u8PlaylistFormatter();
        using var store = LiteDbHostPlaylistStore.Open(DatabasePath, false);
        var saved = await CreatePlaylistAsync(store, "Reviewed");
        var stager = new HostDevicePlaylistStager(store, new CorrectionFixtureResolver(Receiver, trackB, trackA), new([format]), payloads);
        var staged = Value(await stager.StageAsync(library, Caller, new(origin, Guid.NewGuid(), saved.PlaylistId, device, "musicolet", 2, null, null), Token)).Plan;
        var publisher = new HostDeviceExportPublisher(store, store, new FileDeviceExportStepWriter(ActiveShare, work, Receiver, payloads));
        var layout = new DeviceExportLayoutEditor(new MissingEditedDestinations(), payloads, [format]);
        var workflow = new StagedDeviceExportWorkflow(store, store, store, new FileDeviceExportSnapshotReader(ActiveShare, Receiver), publisher,
            payloads, media, layout);
        var planId = staged.PlanId!.Value;
        var initial = Value(await workflow.PrepareAsync(library, Caller, Review(staged.PlanRevision!.Value), Token));
        Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await workflow.PublishAsync(library, Caller,
            new(Guid.NewGuid(), planId, initial.Plan.PlanRevision!.Value), Token)));
        await File.WriteAllTextAsync(refreshed, "fresh audio", Token);

        var blocked = Value(await workflow.PrepareAsync(library, Caller,
            Review(2, new Dictionary<Guid, string> { [trackA] = "Unsafe/Foreign.flac" }.ToImmutableDictionary()), Token));
        var collision = Assert.Single(blocked.Issues);
        Assert.Equal("export-destination-collision", collision.Code);
        var blockedPublish = Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(await workflow.PublishAsync(library, Caller,
            new(Guid.NewGuid(), planId, blocked.Plan.PlanRevision!.Value), Token));

        var edits = new Dictionary<Guid, string> { [trackB] = "New/Move.flac", [trackA] = "New/Fresh.flac" }.ToImmutableDictionary();
        var editIntent = Review(3, edits);
        var corrected = Value(await workflow.PrepareAsync(library, Caller, editIntent, Token));
        Assert.Empty(corrected.Issues);
        var replayed = Value(await workflow.PrepareAsync(library, Caller, editIntent with { RelativePathsByTrack = edits.Reverse().ToImmutableDictionary() }, Token));
        Assert.Equal(corrected.Plan.PlanRevision, replayed.Plan.PlanRevision);
        Assert.Equal(corrected.Operations, replayed.Operations);
        var mismatched = Assert.IsType<HostOutcome<PreparedDeviceExportPublication>.Rejected>(await workflow.PrepareAsync(library, Caller,
            editIntent with { RelativePathsByTrack = edits.SetItem(trackB, "New/Other.flac") }, Token));
        Assert.Equal("operation-intent-mismatch", mismatched.Code);

        var request = new DeviceExportPublicationIntent(Guid.NewGuid(), planId, corrected.Plan.PlanRevision!.Value);
        var confirmation = Assert.IsType<DeviceExportPublicationReservation.ConfirmationRequired>(Value(await workflow.PublishAsync(library, Caller, request, Token))).Proposal;
        var retained = Value(await store.FindExportPlanAsync(library, Caller, new(device, planId, "musicolet", 2), Token)).Plan!;
        var oldExportRetained = Paths(retained).SequenceEqual(["Old/Move.flac", "Old/Fresh.flac", "Old/Move.flac"]) &&
            File.Exists(Path.Combine(ActiveShare, "Old", "Move.flac")) && File.Exists(Path.Combine(ActiveShare, "Old", "Fresh.flac")) &&
            !Directory.Exists(Path.Combine(ActiveShare, "New")) &&
            Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token)).Revision == 1;
        var staleApproval = Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(await workflow.PublishAsync(library, Caller,
            request with { Approval = new(Guid.NewGuid(), planId, request.PlanRevision) }, Token));
        var approved = request with { Approval = new(confirmation.ProposalId, planId, request.PlanRevision) };
        var published = Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await workflow.PublishAsync(library, Caller, approved, Token)));
        Assert.Equal(published, Value(await workflow.PublishAsync(library, Caller, approved, Token)));
        var reloaded = Value(await store.FindExportPlanAsync(library, Caller, new(device, planId, "musicolet", 2), Token)).Plan!;
        var manifest = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
        Assert.Equal(2, manifest.Revision);
        Assert.Equal(["Lists/Reviewed.m3u8", "New/Fresh.flac", "New/Move.flac"], manifest.Files.Select(file => file.Path));
        StageAssertions.VerifyLayoutCorrection(new(collision.Path, blockedPublish.Code, corrected.Plan.PlanRevision!.Value,
            Paths(corrected.Plan), [.. corrected.Operations.Select(Describe)], [.. confirmation.Paths], oldExportRetained, staleApproval.Code,
            await File.ReadAllTextAsync(Path.Combine(ActiveShare, "Lists", "Reviewed.m3u8"), Token),
            await File.ReadAllTextAsync(Path.Combine(ActiveShare, "New", "Move.flac"), Token),
            await File.ReadAllTextAsync(Path.Combine(ActiveShare, "New", "Fresh.flac"), Token),
            !Directory.EnumerateFiles(Path.Combine(ActiveShare, "Old")).Any(), await File.ReadAllTextAsync(foreign, Token),
            await File.ReadAllTextAsync(refreshed, Token), Paths(reloaded)));

        DeviceExportPreviewIntent Review(long revision, ImmutableDictionary<Guid, string>? paths = null) =>
            new(Guid.NewGuid(), planId, revision, device, "musicolet", 2, paths);
        static string[] Paths(DeviceExportPlan value) => [.. value.Entries.OfType<ExportEntry.Playlist>().Single().Tracks.Select(track => track.Destination)];
        static string Describe(DeviceExportFileOperation operation) => operation.Kind == DeviceExportOperationKind.Move
            ? $"Move:{operation.Before!.Path}->{operation.After!.Path}" : $"{operation.Kind}:{(operation.After ?? operation.Before)!.Path}";
    }

    /// <summary>Publishes the exact reviewed playlist and missing audio after the source changes and the store reopens.</summary>
    /// <remarks>2026-11-24: Bind the mock-tested preview assertions to TEST-FILESYNC-001/002 and FR-FILESYNC-001#ac-safety.</remarks>
    [Fact]
    public async Task PreviewRetainsTheReviewedBytesAcrossSourceChangesAndHostRestart()
    {
        Seed();
        SeedTracks();
        var media = Directory.CreateDirectory(Path.Combine(root, "media")).FullName;
        var source = Path.Combine(media, "A.flac");
        await File.WriteAllTextAsync(source, "reviewed audio", Token);
        var work = Directory.CreateDirectory(Path.Combine(root, "publication-work")).FullName;
        var payloads = new FileStagedPlaylistContent(PrivateStage);
        DeviceExportPreviewIntent review;
        PreparedDeviceExportPublication preview;
        bool shareEmpty;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var saved = Value(await new HostPlaylistWriter(store).SaveAsync(library, Caller, new(origin, Guid.NewGuid(),
                new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Headless",
                [new(Guid.NewGuid(), trackB), new(Guid.NewGuid(), trackA)]), Token));
            var staging = CreateStager(store, new ReviewFixtureResolver(Receiver, trackB, trackA));
            var request = new StageDevicePlaylistRequest(origin, Guid.NewGuid(), saved.PlaylistId, device, "musicolet", 2, null, null);
            var staged = Value(await staging.StageAsync(library, Caller, request, Token)).Plan;
            review = new(Guid.NewGuid(), staged.PlanId!.Value, staged.PlanRevision!.Value, device, staged.ProfileId, staged.ProfileRevision);
            var workflow = CreateWorkflow(store);
            var unreviewed = await workflow.PublishAsync(library, Caller, new(Guid.NewGuid(), review.PlanId, review.ExpectedPlanRevision), Token);
            Assert.Equal("export-preview-required", Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(unreviewed).Code);

            preview = Value(await workflow.PrepareAsync(library, Caller, review, Token));
            Assert.Empty(preview.Issues);
            Assert.Null(preview.RemovalProposal);
            shareEmpty = !Directory.EnumerateFileSystemEntries(ActiveShare).Any();
            Assert.Empty(Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token)).Files);
        }

        await File.WriteAllTextAsync(source, "changed after preview", Token);
        using var reopened = LiteDbHostPlaylistStore.Open(DatabasePath, false);
        var resumed = CreateWorkflow(reopened);
        var replayedPreview = Value(await resumed.PrepareAsync(library, Caller, review, Token));
        Assert.Equal(preview.Plan.PlanRevision, replayedPreview.Plan.PlanRevision);
        Assert.Equal(preview.Operations, replayedPreview.Operations);
        var stale = Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(await resumed.PublishAsync(library, Caller,
            new(Guid.NewGuid(), review.PlanId, review.ExpectedPlanRevision), Token));
        var approval = new DeviceExportPublicationIntent(Guid.NewGuid(), review.PlanId, replayedPreview.Plan.PlanRevision!.Value);
        var receipt = Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await resumed.PublishAsync(library, Caller, approval, Token)));
        var replay = Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await resumed.PublishAsync(library, Caller, approval, Token)));
        var manifest = Value(await reopened.ReadPublishedManifestAsync(library, Caller, device, Token));
        StageAssertions.VerifyStagedPlaylistPublish(new(replayedPreview.Plan.PlanRevision.Value,
            [.. replayedPreview.Operations.Select(operation => operation.After!.Path)], shareEmpty,
            await File.ReadAllTextAsync(Path.Combine(ActiveShare, "Library", "Missing.flac"), Token),
            await File.ReadAllTextAsync(Path.Combine(ActiveShare, "Lists", "Headless.m3u8"), Token),
            await File.ReadAllTextAsync(source, Token), manifest.Revision, receipt == replay, stale.Code));
        Assert.False(File.Exists(Path.Combine(ActiveShare, "Library", "Present.flac")));
        Assert.Equal(new[] { "Library/Missing.flac", "Lists/Headless.m3u8" }, manifest.Files.Select(file => file.Path));

        StagedDeviceExportWorkflow CreateWorkflow(LiteDbHostPlaylistStore database)
        {
            var publisher = new HostDeviceExportPublisher(database, database, new FileDeviceExportStepWriter(ActiveShare, work, Receiver, payloads));
            return new(database, database, database, new FileDeviceExportSnapshotReader(ActiveShare, Receiver), publisher, payloads, media);
        }
    }

    [Fact]
    public async Task PublicationReservationResumesTheNextStepAndCommitsOwnershipAndReceiptTogether()
    {
        Seed();
        DeviceExportPublicationIntent intent;
        DeviceExportPlan stagedPlan;
        PendingDeviceExportPublication initial;
        string competing;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var saved = await CreatePlaylistAsync(store, "Ready");
            var resolver = new FixtureResolver(Receiver, trackA, trackB, "Library", "Lists/Ready.m3u8");
            var staging = CreateStager(store, resolver);
            var request = new StageDevicePlaylistRequest(origin, Guid.NewGuid(), saved.PlaylistId, device, "musicolet", 2, null, null);
            stagedPlan = Value(await staging.StageAsync(library, Caller, request, Token)).Plan;
            intent = new(Guid.NewGuid(), stagedPlan.PlanId!.Value, stagedPlan.PlanRevision!.Value);
            var manifest = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
            Assert.Empty(manifest.Files);
            var scope = new DeviceExportShareSnapshot(library, device, Receiver.ShareId, []);
            var preview = Value(new DeviceExportPublicationPlanner().Prepare(stagedPlan, Receiver, manifest, scope, DeviceExportPublicationMode.AddToShare,
                [new(new(ExportContentKind.Audio, trackA), "Library/A.flac", true, true, new string('A', 64)),
                    new(new(ExportContentKind.Playlist, saved.PlaylistId), "Lists/Ready.m3u8", true, true, new string('B', 64))]));
            initial = Assert.IsType<DeviceExportPublicationReservation.Pending>(
                Value(await store.ReservePublicationAsync(library, Caller, intent, preview, Token))).Work;
            Assert.Equal(0, initial.NextStep);

            var competingIntent = intent with { OperationId = Guid.NewGuid() };
            competing = Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(
                await store.ReservePublicationAsync(library, Caller, competingIntent, preview, Token)).Code;
            var changedStage = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Rejected>(await staging.StageAsync(library, Caller,
                request with { OperationId = Guid.NewGuid(), PlanId = stagedPlan.PlanId, ExpectedPlanRevision = stagedPlan.PlanRevision }, Token));
            Assert.Equal("export-publication-in-progress", changedStage.Code);
            var unrelatedMutation = Assert.IsType<HostOutcome<PlaylistSaveResponse>.Rejected>(await new HostPlaylistWriter(store).SaveAsync(library,
                Caller, new(origin, intent.OperationId, new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, "Mismatched intent", []), Token));
            Assert.Equal("operation-intent-mismatch", unrelatedMutation.Code);
            var outOfOrder = Assert.IsType<HostOutcome<int>.Rejected>(await store.RecordPublicationStepAsync(library, Caller, initial.PublicationId, 1, Token));
            Assert.Equal("export-publication-step-mismatch", outOfOrder.Code);
            Assert.Equal(1, Value(await store.RecordPublicationStepAsync(library, Caller, initial.PublicationId, 0, Token)));
        }

        PendingDeviceExportPublication recovered;
        string incomplete;
        string changedIntent;
        long beforeManifestRevision;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            recovered = Assert.IsType<DeviceExportPublicationReservation.Pending>(Value(await store.FindPublicationAsync(library, Caller, intent, Token))).Work;
            Assert.Equal(initial.Operations, recovered.Operations);
            incomplete = Assert.IsType<HostOutcome<CompletedDeviceExportPublication>.Rejected>(
                await store.CompletePublicationAsync(library, Caller, intent, recovered.PublicationId, Token)).Code;
            changedIntent = Assert.IsType<HostOutcome<DeviceExportPublicationReservation?>.Rejected>(await store.FindPublicationAsync(library,
                Caller, intent with { PlanRevision = 99 }, Token)).Code;
            var manifest = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
            beforeManifestRevision = manifest.Revision;
            Assert.Empty(manifest.Files);
            Assert.Equal(1, Value(await store.RecordPublicationStepAsync(library, Caller, recovered.PublicationId, 0, Token)));
            Assert.Equal(2, Value(await store.RecordPublicationStepAsync(library, Caller, recovered.PublicationId, 1, Token)));
            var receipt = Value(await store.CompletePublicationAsync(library, Caller, intent, recovered.PublicationId, Token));
            Assert.Equal(1, receipt.ManifestRevision);
            Assert.Equal(receipt, Value(await store.CompletePublicationAsync(library, Caller, intent, receipt.PublicationId, Token)));
        }

        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var committed = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
            var replay = Assert.IsType<DeviceExportPublicationReservation.Completed>(Value(await store.FindPublicationAsync(library, Caller, intent, Token)));
            StageAssertions.VerifyExportJournal(new(initial.PublicationId, recovered.PublicationId, recovered.NextStep, beforeManifestRevision,
                committed.Revision, [.. committed.Files.Select(file => file.Path)], replay.Receipt.PublicationId, competing, incomplete, changedIntent));
            Assert.Equal(replay.Receipt.ManifestRevision, committed.Revision);

            var observed = new DeviceExportShareSnapshot(library, device, Receiver.ShareId,
                [.. committed.Files.Select(file => new ObservedDeviceExportPath(file.Path, DeviceExportPathKind.File, file.ContentIdentity))]);
            var kept = Assert.Single(committed.Files, file => file.Source.Kind == ExportContentKind.Audio);
            var approvalIntent = intent with { OperationId = Guid.NewGuid() };
            var preview = Value(new DeviceExportPublicationPlanner().Prepare(stagedPlan, Receiver, committed, observed,
                DeviceExportPublicationMode.ReconcileLayout, [new(kept.Source, kept.Path, false, false, null)]));
            var confirmation = Assert.IsType<DeviceExportPublicationReservation.ConfirmationRequired>(
                Value(await store.ReservePublicationAsync(library, Caller, approvalIntent, preview, Token))).Proposal;
            Assert.Equal("Lists/Ready.m3u8", Assert.Single(confirmation.Paths));
            Assert.Null(Value(await store.FindPublicationAsync(library, Caller, approvalIntent, Token)));
            var wrongApproval = approvalIntent with { Approval = new(Guid.NewGuid(), confirmation.PlanId, confirmation.PlanRevision) };
            Assert.Equal("export-removal-approval-stale", Assert.IsType<HostOutcome<DeviceExportPublicationReservation>.Rejected>(
                await store.ReservePublicationAsync(library, Caller, wrongApproval, preview, Token)).Code);
            approvalIntent = approvalIntent with { Approval = new(confirmation.ProposalId, confirmation.PlanId, confirmation.PlanRevision) };
            var nextWork = Assert.IsType<DeviceExportPublicationReservation.Pending>(
                Value(await store.ReservePublicationAsync(library, Caller, approvalIntent, preview, Token))).Work;
            var stillCommitted = Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token));
            Assert.Equal(committed.Revision, stillCommitted.Revision);
            Assert.Equal(committed.Files, stillCommitted.Files);

            Assert.Equal(1, Value(await store.RecordPublicationStepAsync(library, Caller, nextWork.PublicationId, 0, Token)));
            var applied = Value(await store.CompletePublicationAsync(library, Caller, approvalIntent, nextWork.PublicationId, Token));
            Assert.Equal(2, applied.ManifestRevision);
            Assert.Equal(kept, Assert.Single(Value(await store.ReadPublishedManifestAsync(library, Caller, device, Token)).Files));
        }

        using var database = new LiteDatabase(DatabasePath);
        Assert.Empty(database.GetCollection<BsonDocument>("device_export_publication_journal").FindAll());
        Assert.Empty(database.GetCollection<BsonDocument>("device_export_publication_items").FindAll());
        Assert.Equal("Published", database.GetCollection<BsonDocument>("device_exports").FindById(intent.PlanId)["PublicationState"].AsString);
    }

    [Fact]
    public async Task RegisteredLayoutReadsHostCatalogAndJoinsTheSelectedReceiverBeforeCommit()
    {
        Seed();
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "A.flac"), "readable missing-track fixture", Token);
        SeedTracks();
        using var receiverApi = new RegisteredReceiverApi(Receiver);
        using var receiverClient = new HttpClient(receiverApi);
        StageDevicePlaylistRequest request;
        StageDevicePlaylistResponse saved;
        DevicePlaylistSources originalRegistration;
        string wrongLibrary;
        string staleProfile;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var result = await CreatePlaylistAsync(store, "Night Drive");
            var playlist = Value(await store.ReadPlaylistAsync(library, result.PlaylistId, Token))!;
            request = new(origin, Guid.NewGuid(), playlist.PlaylistId, device, "musicolet", 2, null, null);
            var resolver = RegisteredResolver(store, receiverClient, sourceRoot);
            wrongLibrary = Assert.IsType<HostOutcome<ResolvedDevicePlaylist>.Rejected>(
                await resolver.ResolveAsync(playlist with { LibraryId = Guid.NewGuid() }, request, Token)).Code;
            var stale = Assert.IsType<HostOutcome<ResolvedDevicePlaylist>.Rejected>(
                await resolver.ResolveAsync(playlist, request with { ProfileRevision = 1 }, Token));
            staleProfile = stale.Code;
            Assert.Equal(2, stale.ActualRevision);
            Assert.Equal(0, receiverApi.Requests);
            saved = Value(await CreateStager(store, resolver).StageAsync(library, Caller, request, Token));
            Assert.Equal(2, receiverApi.FileRequests);
            originalRegistration = Value(await store.ReadDevicePlaylistSourcesAsync(library, device, [trackA, trackB], Token));
        }

        ChangeReceiverRevision();
        string staleRegistration;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var resolver = RegisteredResolver(new CapturedSources(originalRegistration), receiverClient, sourceRoot);
            var stale = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Rejected>(await CreateStager(store, resolver).StageAsync(library,
                Caller, request with { OperationId = Guid.NewGuid(), PlanId = saved.Plan.PlanId, ExpectedPlanRevision = saved.Plan.PlanRevision }, Token));
            Assert.True(stale.Conflict);
            Assert.Equal(2, stale.ActualRevision);
            staleRegistration = stale.Code;
        }

        using var database = new LiteDatabase(DatabasePath);
        var stored = Assert.Single(database.GetCollection<BsonDocument>("device_exports").FindAll());
        Assert.Equal(1, stored["ManifestRevision"].AsInt64);
        var plannedPlaylist = Assert.Single(saved.Plan.Entries.OfType<ExportEntry.Playlist>());
        StageAssertions.VerifyRegisteredLayout(new(stored["ShareId"].AsString, plannedPlaylist.Format, plannedPlaylist.Destination,
            [.. plannedPlaylist.Tracks.Select(track => track.Destination)], [.. saved.Plan.Entries.OfType<ExportEntry.Audio>().Select(track => track.Destination)],
            wrongLibrary, staleProfile, staleRegistration));
        Assert.Equal("#EXTM3U\n../Lena Vale/Northern Lines/B.flac\n../Lena Vale/Northern Lines/A.flac\n../Lena Vale/Northern Lines/B.flac\n",
            await ReadStagedContentAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(ActiveShare));
    }

    [Fact]
    public async Task OnlyAbsentDeviceAudioIsStagedAndReplaySurvivesAReopenedStore()
    {
        Seed();
        var resolver = new FixtureResolver(Receiver, trackA, trackB);
        StageDevicePlaylistRequest request;
        StageDevicePlaylistResponse accepted;
        using (var store = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var playlist = await CreatePlaylistAsync(store);
            request = new(origin, Guid.NewGuid(), playlist.PlaylistId, device, "musicolet", 2, null, null);
            accepted = Value(await CreateStager(store, resolver).StageAsync(library, Caller, request, Token));
        }

        var actualContent = await ReadStagedContentAsync();
        var acceptedObservation = Observe(accepted, actualContent);
        using (var reopened = LiteDbHostPlaylistStore.Open(DatabasePath, false))
        {
            var stager = CreateStager(reopened, resolver);
            var replay = Value(await stager.StageAsync(library, Caller, request, Token));
            StageAssertions.Verify(acceptedObservation, Observe(replay, await ReadStagedContentAsync()));
            Assert.Equal(request.OperationId, replay.OperationId);
            Assert.Equal(accepted.Plan.Entries.Select(entry => entry.EntryId), replay.Plan.Entries.Select(entry => entry.EntryId));
            Assert.Equal(1, resolver.Calls);

            var wrongIntent = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Rejected>(
                await stager.StageAsync(library, Caller, request with { DeviceId = Guid.NewGuid() }, Token));
            Assert.Equal("operation-intent-mismatch", wrongIntent.Code);
            Assert.True(wrongIntent.Conflict);
            var stale = Assert.IsType<HostOutcome<StageDevicePlaylistResponse>.Rejected>(
                await stager.StageAsync(library, Caller, request with { OperationId = Guid.NewGuid() }, Token));
            Assert.Equal("export-plan-changed", stale.Code);
            Assert.Equal(1, stale.ActualRevision);
            Assert.Equal(1, resolver.Calls);

            var revision = Value(await stager.StageAsync(library, Caller, request with { OperationId = Guid.NewGuid(),
                PlanId = accepted.Plan.PlanId, ExpectedPlanRevision = 1 }, Token));
            Assert.Equal(2, revision.Plan.PlanRevision);
            Assert.Equal(accepted.Plan.Entries.Select(entry => entry.EntryId), revision.Plan.Entries.Select(entry => entry.EntryId));
            Assert.Equal(1, Value(await stager.StageAsync(library, Caller, request, Token)).Plan.PlanRevision);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(ActiveShare));
        Assert.Equal(actualContent, await ReadStagedContentAsync());
        using var rows = new LiteDatabase(DatabasePath);
        Assert.Single(rows.GetCollection<BsonDocument>("device_exports").FindAll());
        var entries = rows.GetCollection<BsonDocument>("device_export_entries").FindAll().ToArray();
        Assert.Equal(2, entries.Length);
        var playlistRow = Assert.Single(entries, row => row["SourceKind"].AsString == "Playlist");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(actualContent))), playlistRow["ContentIdentity"].AsString);
    }

    private HostDevicePlaylistStager CreateStager(LiteDbHostPlaylistStore store, IHostDevicePlaylistResolver resolver) =>
        new(store, resolver, new([new M3u8PlaylistFormatter()]), new FileStagedPlaylistContent(PrivateStage));

    private RegisteredDevicePlaylistResolver RegisteredResolver(IHostDeviceExportSources sources, HttpClient client, string sourceRoot) =>
        new(sources, new SyncthingReceiverInventory(client), new FixtureTokenProvider(Receiver),
            new AndroidLayoutProfiles([new M3u8PlaylistFormatter()]).LoadBuiltIn(), sourceRoot);

    private void Seed(RegisteredAndroidExport? receiver = null)
    {
        using (LiteDbHostPlaylistStore.Open(DatabasePath, true)) { }
        Directory.CreateDirectory(ActiveShare);
        using var db = new LiteDatabase(DatabasePath);
        db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
        {
            ["_id"] = origin, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active"
        });
        TestAndroidRegistration.Seed(db, receiver ?? Receiver);
    }

    private async Task<PlaylistSaveResponse> CreatePlaylistAsync(LiteDbHostPlaylistStore store, string name = "Mix") => Value(await new HostPlaylistWriter(store)
        .SaveAsync(library, Caller, new(origin, Guid.NewGuid(), new(PlaylistSaveSourceKind.Draft, Guid.NewGuid()), null, null, name,
            [new(Guid.NewGuid(), trackB), new(Guid.NewGuid(), trackA), new(Guid.NewGuid(), trackB)]), Token));

    private void SeedTracks()
    {
        using var database = new LiteDatabase(DatabasePath);
        var artist = Guid.NewGuid();
        var album = Guid.NewGuid();
        database.GetCollection<BsonDocument>("catalog_artists").Insert(new BsonDocument { ["_id"] = artist, ["LibraryId"] = library, ["Name"] = "Lena Vale" });
        database.GetCollection<BsonDocument>("catalog_albums").Insert(new BsonDocument
            { ["_id"] = album, ["LibraryId"] = library, ["Title"] = "Northern Lines", ["AlbumArtistId"] = artist });
        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        tracks.Insert(new BsonDocument { ["_id"] = trackA, ["LibraryId"] = library, ["AlbumId"] = album, ["ArtistId"] = artist, ["RelativePath"] = "A.flac" });
        tracks.Insert(new BsonDocument { ["_id"] = trackB, ["LibraryId"] = library, ["AlbumId"] = album, ["ArtistId"] = artist, ["RelativePath"] = "B.flac" });
    }

    private void ChangeReceiverRevision()
    {
        using var database = new LiteDatabase(DatabasePath);
        var registry = database.GetCollection<BsonDocument>("registered_instances");
        var row = registry.FindById(device);
        row["AndroidExport"].AsDocument["Revision"] = 2L;
        Assert.True(registry.Update(row));
    }

    private Task<string> ReadStagedContentAsync() => File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(PrivateStage)), Token);

    private StageObservation Observe(StageDevicePlaylistResponse result, string content)
    {
        var playlist = Assert.Single(result.Plan.Entries.OfType<ExportEntry.Playlist>());
        Assert.Equal("m3u8", playlist.Format);
        Assert.Equal("Playlists/Mix.m3u8", playlist.Destination);
        return new(result.Plan.PlanId!.Value, result.Plan.PlanRevision!.Value,
            [.. playlist.Tracks.Select(track => TrackName(track.TrackId))],
            [.. result.Plan.Entries.OfType<ExportEntry.Audio>().Select(track => TrackName(track.TrackId))], content);
    }

    private string TrackName(Guid id) => id == trackA ? "A" : id == trackB ? "B" : throw new ArgumentException("Unknown track.", nameof(id));
    private static T Value<T>(HostOutcome<T> outcome) => Assert.IsType<HostOutcome<T>.Success>(outcome).Value;

    private sealed class ReviewFixtureResolver(RegisteredAndroidExport receiver, Guid present, Guid missing) : IHostDevicePlaylistResolver
    {
        public Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token) =>
            Task.FromResult<HostOutcome<ResolvedDevicePlaylist>>(new HostOutcome<ResolvedDevicePlaylist>.Success(new(receiver,
                new(playlist.PlaylistId, request.DeviceId, "musicolet", 2, "m3u8", "Lists/Headless.m3u8", false,
                    [new(present, "Library/Present.flac", true, false), new(missing, "Library/Missing.flac", false, true)]))));
    }

    private sealed class CorrectionFixtureResolver(RegisteredAndroidExport receiver, Guid move, Guid fresh) : IHostDevicePlaylistResolver
    {
        public Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token) =>
            Task.FromResult<HostOutcome<ResolvedDevicePlaylist>>(new HostOutcome<ResolvedDevicePlaylist>.Success(new(receiver,
                new(playlist.PlaylistId, request.DeviceId, "musicolet", 2, "m3u8", "Lists/Reviewed.m3u8", false,
                    [new(move, "Old/Move.flac", false, true), new(fresh, "Old/Fresh.flac", false, true), new(move, "Old/Move.flac", false, true)]))));
    }

    private sealed class MissingEditedDestinations : IHostDeviceExportPresence
    {
        public Task<HostOutcome<ImmutableDictionary<string, bool>>> ReadAsync(RegisteredAndroidExport target, IEnumerable<string> paths, CancellationToken token) =>
            Task.FromResult<HostOutcome<ImmutableDictionary<string, bool>>>(new HostOutcome<ImmutableDictionary<string, bool>>.Success(
                paths.ToImmutableDictionary(path => path, _ => false, StringComparer.Ordinal)));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class SenderRegistry(RegisteredAndroidExport receiver, string share) : ISyncthingExportSenderRegistry
    {
        public const string SenderId = "fixture-sender";
        public SyncthingExportSender? Find(RegisteredAndroidExport target) => target == receiver ? new(new("http://127.0.0.1:8384/"), SenderId, share) : null;
        public ValueTask<string?> GetTokenAsync(RegisteredAndroidExport target, CancellationToken token) => ValueTask.FromResult<string?>("fixture-sender-token");
    }

    private sealed class SenderScanApi(RegisteredAndroidExport receiver, string share) : HttpMessageHandler
    {
        public string Identity { get; set; } = SenderRegistry.SenderId;
        public bool IncludeAnotherPeer { get; set; }
        public List<string> ScannedFolders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-sender-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            if (path == "/rest/system/status" && request.Method == HttpMethod.Get)
                response.Content = JsonContent.Create(new { myID = Identity });
            else if (path == $"/rest/config/folders/{Uri.EscapeDataString(receiver.ShareId)}" && request.Method == HttpMethod.Get)
            {
                var devices = new List<string> { SenderRegistry.SenderId, receiver.SyncthingDeviceId };
                if (IncludeAnotherPeer)
                    devices.Add("unrelated-peer");
                response.Content = JsonContent.Create(new { id = receiver.ShareId, type = "sendonly", path = share, paused = false,
                    devices = devices.Select(deviceID => new { deviceID }) });
            }
            else if (path == "/rest/db/scan" && request.Method == HttpMethod.Post)
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("folder", Assert.Single(query.Keys));
                ScannedFolders.Add(Assert.Single(query["folder"])!);
                if (ScannedFolders.Count == 1)
                    response.StatusCode = HttpStatusCode.ServiceUnavailable;
            }
            else
                throw new InvalidOperationException($"Unexpected scan request {request.Method} {path}");
            return Task.FromResult(response);
        }
    }

    private sealed class TransferIndexApi(RegisteredAndroidExport receiver, string share) : HttpMessageHandler
    {
        private long receiverSequence;
        public bool ReceiverHasCurrentPlaylist { get; set; }
        public bool ReceiverHasTombstone { get; set; }
        public bool UnstableReceiver { get; set; }
        public List<string> SenderReadPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!;
            var isReceiver = address.Host == receiver.ReceiverEndpoint.Host;
            Assert.Equal(isReceiver ? "fixture-key" : "fixture-sender-token", request.Headers.Authorization?.Parameter);
            var query = QueryHelpers.ParseQuery(address.Query);
            object? body = address.AbsolutePath switch
            {
                "/rest/system/status" => new { myID = isReceiver ? receiver.SyncthingDeviceId : SenderRegistry.SenderId },
                var path when path == $"/rest/config/folders/{receiver.ShareId}" && !isReceiver => new
                {
                    id = receiver.ShareId, type = "sendonly", path = share, paused = false,
                    devices = new[] { new { deviceID = SenderRegistry.SenderId }, new { deviceID = receiver.SyncthingDeviceId } }
                },
                "/rest/db/scan" when !isReceiver && request.Method == HttpMethod.Post && query["folder"] == receiver.ShareId => null,
                "/rest/db/status" when query["folder"] == receiver.ShareId => new
                {
                    state = "idle", ignorePatterns = false, sequence = isReceiver && UnstableReceiver ? ++receiverSequence : 1,
                    stateChanged = DateTimeOffset.UnixEpoch
                },
                "/rest/db/file" when query["folder"] == receiver.ShareId => File(Assert.Single(query["file"])!, isReceiver),
                _ => throw new InvalidOperationException($"Unexpected index request {request.Method} {address.AbsolutePath}")
            };
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            if (body is not null)
                reply.Content = JsonContent.Create(body, body.GetType());
            return Task.FromResult(reply);
        }

        private object File(string path, bool isReceiver)
        {
            if (!isReceiver)
                SenderReadPaths.Add(path);
            var (bytes, version, deleted) = path switch
            {
                "Music/new.flac" => (4L, "SEND:2", false),
                "Playlists/Ready.m3u8" => (5L, isReceiver && !ReceiverHasCurrentPlaylist ? "SEND:1" : "SEND:3", false),
                "Playlists/Retired.m3u8" when isReceiver && !ReceiverHasTombstone => (7L, "SEND:1", false),
                "Playlists/Retired.m3u8" => (0L, "SEND:4", true),
                _ => throw new InvalidOperationException($"Unexpected indexed path {path}")
            };
            return new { local = new { name = path, size = bytes, deleted, invalid = false, ignored = false,
                mustRescan = false, type = "FILE_INFO_TYPE_FILE", version = new[] { version } } };
        }
    }

    private sealed class FixtureResolver(RegisteredAndroidExport receiver, Guid a, Guid b, string audioDirectory = "Music",
        string playlistDestination = "Playlists/Mix.m3u8") : IHostDevicePlaylistResolver
    {
        public int Calls { get; private set; }

        public Task<HostOutcome<ResolvedDevicePlaylist>> ResolveAsync(SavedHostPlaylist playlist, StageDevicePlaylistRequest request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<HostOutcome<ResolvedDevicePlaylist>>(new HostOutcome<ResolvedDevicePlaylist>.Success(new(receiver,
                new(playlist.PlaylistId, request.DeviceId, "musicolet", 2, "m3u8", playlistDestination, false,
                    [new(b, $"{audioDirectory}/B.flac", true, false), new(a, $"{audioDirectory}/A.flac", false, true),
                        new(b, $"{audioDirectory}/B.flac", true, false)]))));
        }
    }

    private sealed class CapturedSources(DevicePlaylistSources snapshot) : IHostDeviceExportSources
    {
        public Task<HostOutcome<DevicePlaylistSources>> ReadDevicePlaylistSourcesAsync(Guid libraryId, Guid deviceId,
            IEnumerable<Guid> trackIds, CancellationToken token) => Task.FromResult<HostOutcome<DevicePlaylistSources>>(new HostOutcome<DevicePlaylistSources>.Success(snapshot));
    }

    private sealed class FixtureTokenProvider(RegisteredAndroidExport receiver) : IRegisteredSyncthingTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(Guid libraryId, Guid deviceId, CancellationToken token) =>
            ValueTask.FromResult<string?>(libraryId == receiver.LibraryId && deviceId == receiver.DeviceId ? "fixture-key" : null);
    }

    private sealed class RegisteredReceiverApi(RegisteredAndroidExport registration) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int FileRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(registration.ReceiverEndpoint.Host, request.RequestUri!.Host);
            Assert.Equal("fixture-key", request.Headers.Authorization?.Parameter);
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var response = request.RequestUri.AbsolutePath switch
            {
                "/rest/system/status" => Json(new { myID = registration.SyncthingDeviceId }),
                "/rest/db/status" => Json(new { state = "idle", sequence = 2, stateChanged = "2026-11-24T20:00:00Z", ignorePatterns = false }),
                "/rest/db/file" => ReadFile(query["file"].ToString()),
                _ => throw new InvalidOperationException("Unexpected receiver route.")
            };
            if (request.RequestUri.AbsolutePath != "/rest/system/status")
                Assert.Equal(registration.ShareId, query["folder"].ToString());
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage ReadFile(string path)
        {
            FileRequests++;
            return path switch
            {
                "Lena Vale/Northern Lines/A.flac" => new(HttpStatusCode.NotFound) { Content = new StringContent("No such object in the index\n") },
                "Lena Vale/Northern Lines/B.flac" => Json(new { local = new { name = path, type = "FILE_INFO_TYPE_FILE", deleted = false,
                    ignored = false, invalid = false, mustRescan = false } }),
                _ => throw new InvalidOperationException("Unexpected receiver destination.")
            };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
