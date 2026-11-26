using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Closes and reopens the desktop around offline staging, a lost host receipt, and explicit conflict decisions.</summary>
    /// <remarks>2026-11-25: Bind TEST-RECORDS-002 to real stores, CQRS, and authenticated HTTPS.</remarks>
    /// <remarks>2026-11-26: Verify stable offline publication intent and preserved conflict state for FR-RECORDS-001#ac-offline.</remarks>
    /// <remarks>2026-11-26: Reuse the HTTPS and LiteDB fixture in the named FR-RECORDS-001#ac-offline case.</remarks>
    internal async Task OfflineListenerOutboxReplaysExactOperationsAndRetainsConflictsAcrossSnapshot()
    {
        const string peerCredential = "outbox-peer";
        var peer = Guid.NewGuid();
        var track = Guid.NewGuid();
        var scope = new LibraryRecordScope(library, instance);
        var originRequest = new RequestContext(Guid.NewGuid(), instance);
        var firstFavorite = Command();
        var secondFavorite = Command();
        var recordPlay = Command();
        var play = new RecordPlay(Guid.NewGuid(), track, new DateTimeOffset(2026, 11, 25, 0, 37, 1, TimeSpan.Zero).AddTicks(23));
        var desktopPath = Path.Combine(root, "listener-outbox-desktop.db");
        var hostPath = Path.Combine(root, "listener-outbox-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "listener-music")).FullName;
        var listenerCredentials = new TokenProvider(library, instance, secret);
        int pendingBefore;
        bool offlineFavorite;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopPath, allowCreate: true))
        {
            var offline = new UnexpectedLibraryRecordHost();
            var local = new LibraryRecordHandlers(scope, desktop, offline).Register(new()).Build();
            var displayed = Success(await local.QueryAsync(new GetFavorite(track), originRequest, Token));
            Assert.Equal(0, displayed.Revision);
            Assert.True(Success(await local.SendAsync(new SetFavorite(track, true, displayed.Revision), firstFavorite, Token)).PendingSync);
            var staged = Success(await local.SendAsync(new SetFavorite(track, false, displayed.Revision), secondFavorite, Token));
            Assert.True(staged.PendingSync);
            offlineFavorite = staged.IsFavorite;
            Assert.True(Success(await local.SendAsync(play, recordPlay, Token)).PendingSync);
            Assert.Equal(staged, Success(await local.SendAsync(new SetFavorite(track, false, displayed.Revision), secondFavorite, Token)));
            Assert.IsType<Outcome<FavoriteState>.Conflict>(await local.SendAsync(new SetFavorite(track, true, displayed.Revision), secondFavorite, Token));
            var recentlyPlayed = Assert.Single(Success(await local.QueryAsync(new GetListeningHistory(5), originRequest, Token)));
            Assert.Equal(new RecordedPlay(play.PlayId, track, play.PlayedAtUtc, true), recentlyPlayed);
            pendingBefore = Success(await desktop.ReadReplicaAsync(library, instance, Token)).PendingCount;
        }

        SeedHost(hostPath);
        using (var db = new LiteDatabase(hostPath))
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = peer, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
            { [secret] = new(library, instance), [peerCredential] = new(library, peer) });
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        await using var host = LibraryHostStartup.Build(builder, new(library, hostPath, music, Path.Combine(root, "listener-stage"),
            Path.Combine(root, "listener-work"), []), verifier, credentials, credentials);
        await host.StartAsync(Token);
        var address = new Uri(host.Urls.Single());
        using var transport = new LostLibraryRecordReceipt(CreateClientHandler(certificate), firstFavorite.OperationId);
        using var http = new HttpClient(transport);
        using var peerHttp = new HttpClient(CreateClientHandler(certificate));
        var other = new HttpLibraryRecordHost(peerHttp, address, library, peer, new TokenProvider(library, peer, peerCredential));
        var gateway = new HttpLibraryRecordHost(http, address, library, instance, listenerCredentials);
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopPath, allowCreate: false))
        {
            var bus = Connected(desktop);
            var firstPull = await Resume(bus);
            Assert.Equal(1, firstPull.Cursor);
            Assert.Equal(3, firstPull.PendingCount);
            Assert.True(Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token)).PendingSync);
        }

        bool originalOps;
        bool stableRetry;
        int historyCount;
        int pendingAfter;
        long secondBaseline;
        bool conflictSurvived;
        bool reapplied;
        bool keptHost;
        using (var desktop = LiteDbDesktopPlaylistStore.Open(desktopPath, allowCreate: false))
        {
            var bus = Connected(desktop);
            var caughtUp = await Resume(bus);
            Assert.Equal(3, caughtUp.Cursor);
            pendingAfter = caughtUp.PendingCount;
            var attempts = transport.Submissions.ToArray();
            originalOps = attempts.Select(item => item.OperationId).Distinct().SequenceEqual([firstFavorite.OperationId, secondFavorite.OperationId, recordPlay.OperationId]);
            stableRetry = attempts.Length == 4 && attempts[0] == attempts[1];
            secondBaseline = Assert.IsType<LibraryRecordIntent.Favorite>(attempts.Single(item => item.OperationId == secondFavorite.OperationId).Change).ExpectedRevision;
            var history = Success(await bus.QueryAsync(new GetListeningHistory(5), originRequest, Token));
            historyCount = history.Select(item => item.PlayId).Distinct().Count();
            Assert.Equal(new RecordedPlay(play.PlayId, track, play.PlayedAtUtc, false), Assert.Single(history));
            var inactive = Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token));
            Assert.Equal(new FavoriteState(track, false, 2, false), inactive);

            var concurrent = Success(await other.PublishAsync(new LibraryRecordIntent.Favorite(track, true, inactive.Revision), Guid.NewGuid(), Token));
            var conflictedOp = Command();
            Success(await bus.SendAsync(new SetFavorite(track, false, inactive.Revision), conflictedOp, Token));
            var snapshot = await Resume(bus, ReplicaRecoveryMode.Snapshot);
            var draft = Assert.Single(snapshot.Conflicts);
            Assert.Equal(concurrent.FeedRevision, snapshot.Cursor);
            conflictSurvived = draft.ConflictId == conflictedOp.OperationId && draft.HostRevision == concurrent.RecordRevision &&
                draft.LocalSummary == "Not a favorite" && draft.HostSummary == "Favorite";
            Assert.True(Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token)).IsFavorite);
            Assert.IsType<Outcome<ReplicaConflictReceipt>.Conflict>(await bus.SendAsync(
                new ResolveReplicaConflict(draft.ConflictId, ReplicaResolution.ReapplyLocal, inactive.Revision), Command(), Token));
            var resolution = new ResolveReplicaConflict(draft.ConflictId, ReplicaResolution.ReapplyLocal, draft.HostRevision);
            var reapplyOp = Command();
            var accepted = Success(await bus.SendAsync(resolution, reapplyOp, Token));
            Assert.Equal(accepted, Success(await bus.SendAsync(resolution, reapplyOp, Token)));
            Assert.Equal(1, (await ReadStatus(bus)).PendingCount);
            var afterResolution = await Resume(bus);
            var newAttempt = Assert.IsType<LibraryRecordIntent.Favorite>(transport.Submissions.Single(item => item.OperationId == reapplyOp.OperationId).Change);
            reapplied = afterResolution.Conflicts.IsEmpty && newAttempt.ExpectedRevision == concurrent.RecordRevision &&
                !Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token)).IsFavorite;

            var current = Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token));
            var finalHost = Success(await other.PublishAsync(new LibraryRecordIntent.Favorite(track, true, current.Revision), Guid.NewGuid(), Token));
            Success(await bus.SendAsync(new SetFavorite(track, false, current.Revision), Command(), Token));
            var finalPull = await Resume(bus);
            var lastDraft = Assert.Single(finalPull.Conflicts);
            Success(await bus.SendAsync(new ResolveReplicaConflict(lastDraft.ConflictId, ReplicaResolution.KeepHost, lastDraft.HostRevision), Command(), Token));
            var kept = Success(await bus.QueryAsync(new GetFavorite(track), originRequest, Token));
            keptHost = kept == new FavoriteState(track, true, finalHost.RecordRevision, false) && (await ReadStatus(bus)).Conflicts.IsEmpty;
        }
        LibraryRecordOutboxAssertions.Verify(new(pendingBefore, offlineFavorite, originalOps, secondBaseline, historyCount,
            pendingAfter, stableRetry, conflictSurvived, reapplied, keptHost));

        CommandContext Command() => new(originRequest, Guid.NewGuid());
        RequestDispatcher Connected(LiteDbDesktopPlaylistStore desktop)
        {
            var replica = new HttpCatalogReplicaHost(http, address, library, instance, listenerCredentials, pageSize: 2);
            return new CatalogReplicaHandlers(library, instance, replica, desktop, new LibraryRecordHandlers(scope, desktop, gateway)).Register(new()).Build();
        }
        async Task<ReplicationState> ReadStatus(RequestDispatcher bus) => Success(await bus.QueryAsync(new GetReplicationStatus(library, instance), originRequest, Token));
        async Task<ReplicationState> Resume(RequestDispatcher bus, ReplicaRecoveryMode mode = ReplicaRecoveryMode.Incremental)
        {
            var state = await ReadStatus(bus);
            return Success(await bus.SendAsync(new ResumeReplication(library, instance, mode, state.Cursor), Command(), Token));
        }
        static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;
    }

    /// <summary>Fails if offline commands try to send before reconnect.</summary>
    private sealed class UnexpectedLibraryRecordHost : ILibraryRecordHost
    {
        public Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token) =>
            throw new InvalidOperationException("The staged listener command reached the host before reconnect.");
    }

    /// <summary>Loses the first accepted record response while recording submissions for replay evidence.</summary>
    private sealed class LostLibraryRecordReceipt(HttpMessageHandler inner, Guid interrupted) : DelegatingHandler(inner)
    {
        internal List<PublishLibraryRecordRequest> Submissions { get; } = [];
        private bool lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            PublishLibraryRecordRequest? publication = null;
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/records", StringComparison.Ordinal))
            {
                publication = await request.Content!.ReadFromJsonAsync<PublishLibraryRecordRequest>(token);
                Submissions.Add(Assert.IsType<PublishLibraryRecordRequest>(publication));
            }
            var response = await base.SendAsync(request, token);
            if (lost || publication?.OperationId != interrupted || !response.IsSuccessStatusCode)
                return response;
            lost = true;
            response.Dispose();
            throw new HttpRequestException("Interrupted after committing the listener record.");
        }
    }
}
