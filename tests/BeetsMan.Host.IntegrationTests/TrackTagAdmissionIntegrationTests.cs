using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies a track-specific snapshot and a single edit receipt after loss and restart.</summary>
    /// <remarks>2026-11-24: Bind SCR39 and FR-BEETS-001#ac-edit and ac-recover through the Toolkit commands, HTTPS, and LiteDB.</remarks>
    /// <remarks>2026-11-26: Include the tagged revision checks in the named FR-BEETS-001#ac-edit case.</remarks>
    internal async Task TrackTagDraftReadsTheSelectedRevisionAndRecoversOneAcceptedEdit()
    {
        var database = Path.Combine(root, "track-tags.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var track = Guid.NewGuid();
        var otherTrack = Guid.NewGuid();
        var artist = Guid.NewGuid();
        var album = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("catalog_artists").Insert(new BsonDocument
                { ["_id"] = artist, ["LibraryId"] = library, ["Name"] = "Lena Vale" });
            db.GetCollection<BsonDocument>("catalog_albums").Insert(new BsonDocument
                { ["_id"] = album, ["LibraryId"] = library, ["AlbumArtistId"] = artist, ["Title"] = "Northern Lines" });
            var tracks = db.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(new BsonDocument { ["_id"] = track, ["LibraryId"] = library, ["ArtistId"] = artist, ["AlbumId"] = album,
                ["BeetsTrackId"] = 27L, ["RelativePath"] = "Lena Vale/Northern Lines/Harbor Lights.flac", ["Revision"] = 184L,
                ["Title"] = "Harbor Lights", ["Genre"] = "Ambient", ["Year"] = 2023, ["DiscNumber"] = 1, ["TrackNumber"] = 1 });
            tracks.Insert(new BsonDocument { ["_id"] = otherTrack, ["LibraryId"] = Guid.NewGuid(), ["Revision"] = 184L, ["Title"] = "Foreign track" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 185L });
        }

        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var selectedPath = TrackTagRoutes.Path(library, track);
        var foreignPath = TrackTagRoutes.Path(library, otherTrack);
        var changes = ImmutableDictionary<string, string?>.Empty.Add("Genre", "Downtempo").Add("year", null);
        var unadmitted = new EditTrackTagsRequest(instance, Guid.NewGuid(), 184, changes);
        TrackTagSnapshot snapshot;
        LibraryJobReceipt accepted;
        EditTrackTagsRequest submitted;
        EditTrackTagsRequest retried;
        bool retained;
        int foreignRead;
        int foreignEdit;
        string unsupported;
        (string Code, long? ActualRevision) stale;

        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var address = new Uri(host.Urls.Single());
            using var direct = Direct(address);
            using var unregistered = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            using var unauthenticatedRead = await unregistered.GetAsync(selectedPath, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedRead.StatusCode);
            using var foreign = await direct.GetAsync(foreignPath, Token);
            foreignRead = (int)foreign.StatusCode;
            using var rejectedEdit = await direct.PostAsJsonAsync(foreignPath, unadmitted, Token);
            foreignEdit = (int)rejectedEdit.StatusCode;
            stale = await RejectAsync(direct, unadmitted with { ExpectedCatalogRevision = 183 }, HttpStatusCode.Conflict);
            (unsupported, _) = await RejectAsync(direct, unadmitted with
                { Changes = ImmutableDictionary<string, string?>.Empty.Add("path", "elsewhere.flac") }, HttpStatusCode.BadRequest);

            using var wire = new TrackTagEditTransport(CreateClientHandler(certificate));
            using var http = new HttpClient(wire);
            var gateway = new HttpImportIntakeHost(http, address, library, new TokenProvider(library, instance, secret));
            var dispatcher = new ImportIntakeHandlers(library, new ImportFilePicker(), gateway).Register(new RequestDispatcher.Builder()).Build();
            var services = new PresentationServices(dispatcher, new InlineScheduler(), instance);
            using var viewModel = new TagEditViewModel(services, Guid.NewGuid());
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenCommand.ExecuteAsync(track);
            snapshot = Assert.IsType<TrackTagSnapshot>(viewModel.Track);
            viewModel.Changes = changes;
            await viewModel.SaveCommand.ExecuteAsync(null);
            retained = viewModel.Changes == changes && viewModel.AcceptedJob is null &&
                viewModel.Failure is { Code: "track-tag-edit-host-unreachable", CanRetry: true };
            await viewModel.SaveCommand.ExecuteAsync(null);
            accepted = Assert.IsType<LibraryJobReceipt>(viewModel.AcceptedJob);
            Assert.Equal(2, wire.Attempts.Count);
            submitted = wire.Attempts[0];
            retried = wire.Attempts[1];
            Assert.Equal(instance, submitted.OriginInstanceId);
            Assert.Equal(184, submitted.ExpectedCatalogRevision);
        }

        TrackTagEditReceipt replay;
        string changedReplay;
        string pending;
        ImportJobSnapshot inspected;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var direct = Direct(new(host.Urls.Single()));
            var canonical = changes.Remove("Genre").Add("genre", "Downtempo");
            using var repeated = await direct.PostAsJsonAsync(selectedPath, submitted with { Changes = canonical }, Token);
            repeated.EnsureSuccessStatusCode();
            replay = (await repeated.Content.ReadFromJsonAsync<TrackTagEditReceipt>(Token))!;
            (changedReplay, _) = await RejectAsync(direct, submitted with { Changes = canonical.SetItem("genre", "Rock") }, HttpStatusCode.Conflict);
            (pending, _) = await RejectAsync(direct, unadmitted, HttpStatusCode.Conflict);
            inspected = (await direct.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, accepted.JobId), Token))!;
            Assert.Equal(replay.JobId, inspected.JobId);
        }

        using (var db = new LiteDatabase(database))
        {
            var jobs = db.GetCollection<BsonDocument>("library_jobs").FindAll().ToArray();
            var job = Assert.Single(jobs);
            Assert.Equal(accepted.JobId, job["_id"].AsGuid);
            Assert.Equal(track, job["TrackId"].AsGuid);
            Assert.Equal(27, job["BeetsTrackId"].AsInt64);
            Assert.Equal(184, job["ExpectedTrackRevision"].AsInt64);
            var patch = JsonSerializer.Deserialize<SortedDictionary<string, string?>>(job["TagChanges"].AsString)!;
            var original = db.GetCollection<BsonDocument>("catalog_tracks").FindById(track);
            Assert.Equal("Ambient", original["Genre"].AsString);
            Assert.Equal(2023, original["Year"].AsInt32);
            Assert.Empty(db.GetCollection<BsonDocument>("catalog_changes").FindAll());
            Assert.Empty(db.GetCollection<BsonDocument>("notification_deliveries").FindAll());
            var revision = db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
            TrackTagAdmissionAssertions.Verify(new(track, snapshot.TrackId, snapshot.CatalogRevision,
                [.. snapshot.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => tag.Value)], foreignRead, foreignEdit,
                stale.Code, stale.ActualRevision!.Value, unsupported, changedReplay, pending, accepted.JobId, replay.JobId, submitted.OperationId,
                retried.OperationId, inspected.Phase.ToString(), jobs.Length, revision, [.. patch.Select(tag => $"{tag.Key}={tag.Value ?? "<clear>"}")], retained));
        }

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, database, music, Path.Combine(root, "private"), Path.Combine(root, "work"), []),
                new Verifier(new(library, instance), secret, null), credentials, credentials);
        }

        HttpClient Direct(Uri address)
        {
            var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return client;
        }

        async Task<(string Code, long? ActualRevision)> RejectAsync(HttpClient client, EditTrackTagsRequest request, HttpStatusCode expected)
        {
            using var response = await client.PostAsJsonAsync(selectedPath, request, Token);
            Assert.Equal(expected, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
            var revision = body.GetProperty("actualRevision");
            return (body.GetProperty("code").GetString()!, revision.ValueKind == JsonValueKind.Number ? revision.GetInt64() : null);
        }
    }

    /// <summary>Records each desktop command intent and drops the first durable acknowledgement.</summary>
    private sealed class TrackTagEditTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<EditTrackTagsRequest> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return await base.SendAsync(request, cancellationToken);
            Attempts.Add((await request.Content!.ReadFromJsonAsync<EditTrackTagsRequest>(cancellationToken))!);
            var response = await base.SendAsync(request, cancellationToken);
            if (Attempts.Count != 1 || !response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw new HttpRequestException("The fixture discarded the committed tag edit receipt.");
        }
    }
}
