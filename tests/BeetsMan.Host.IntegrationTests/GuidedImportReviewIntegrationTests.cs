using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies the persisted album review, Toolkit decisions, and the HTTPS replay boundary after host restart.</summary>
    /// <remarks>2026-11-24: Bind the STB08 choice mock assertions to real transactions for FR-BEETS-001#ac-import, ac-recover and TEST-BEETS-002.</remarks>
    /// <remarks>2026-11-26: Reuse the proven scenarios for the named FR-BEETS-001#ac-import case.</remarks>
    internal async Task GuidedImportReviewJournalsExactlyOneAlbumChoice(ImportChoiceKind kind)
    {
        var database = Path.Combine(root, "review.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var privateStage = Path.Combine(root, "private");
        var other = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = other, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 41L });
        }

        var bytes = Encoding.UTF8.GetBytes("fixture audio, private stage only");
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var upload = new BeginImportUploadRequest(instance, Guid.NewGuid(), [new("North.flac", bytes.Length, digest),
            new("South.flac", bytes.Length, digest), new("Harbor.flac", bytes.Length, digest)]);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var identity = new Verifier(new(library, instance), secret, other);
        using var certificate = CreateCertificate();
        ImportJobSnapshot offered;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var client = CreateClient(host);
            var begin = await Send(client, HttpMethod.Post, ImportIntakeRoutes.BeginPath(library), JsonContent.Create(upload), secret);
            Assert.Equal(200, begin.Status);
            for (var index = 0; index < upload.Files.Length; index++)
                Assert.Equal(200, (await Send(client, HttpMethod.Put, ImportIntakeRoutes.PartPath(library, upload.UploadId, index),
                    new ByteArrayContent(bytes), secret)).Status);
            var start = await Send(client, HttpMethod.Post, ImportIntakeRoutes.StartPath(library),
                JsonContent.Create(new StartImportRequest(instance, Guid.NewGuid(), upload.UploadId)), secret);
            Assert.Equal(200, start.Status);
            var accepted = Deserialize<StartImportReceipt>(start.Body);
            var workerReview = host.Services.GetRequiredService<HostImportReview>();
            var result = await workerReview.OfferAsync(library, accepted.JobId, upload.UploadId,
                [new("fixture-source:album-42", "Fixture Album", "Fixture Artist"), new("fixture-source:album-17", "Alternate Album", "Fixture Artist")], Token);
            offered = Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(result).Value;
            var duplicate = Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(await workerReview.OfferAsync(library, accepted.JobId, upload.UploadId,
                [new("fixture-source:album-99", "Changed after offer", "Fixture Artist")], Token)).Value;
            Assert.Equal(offered.Review!.ItemId, duplicate.Review!.ItemId);
            Assert.Equal(offered.Review.Matches.ToArray(), duplicate.Review.Matches.ToArray());
        }

        var statusPath = ImportIntakeRoutes.JobPath(library, offered.JobId);
        var choicePath = ImportIntakeRoutes.ChoicePath(library, offered.JobId);
        ChooseImportRequest approved;
        ChooseImportReceipt firstReceipt;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var client = CreateClient(host);
            var foreignRead = await Send(client, HttpMethod.Get, statusPath, null, RevokedCredential);
            var hostRead = await Send(client, HttpMethod.Get, statusPath, null, secret);
            Assert.Equal(200, hostRead.Status);
            Assert.DoesNotContain("fixture-source:", hostRead.Body, StringComparison.Ordinal);
            var recovered = Deserialize<ImportJobSnapshot>(hostRead.Body);
            var review = Assert.IsType<ImportAlbumReview>(recovered.Review);
            var probe = new ChooseImportRequest(instance, Guid.NewGuid(), review.ItemId, 1, ImportChoiceKind.KeepTags);
            var foreignChoice = await Send(client, HttpMethod.Post, choicePath,
                JsonContent.Create(probe with { OriginInstanceId = other, ExpectedRevision = recovered.Revision }), RevokedCredential);
            var stale = await Send(client, HttpMethod.Post, choicePath, JsonContent.Create(probe), secret);
            Assert.Equal(409, stale.Status);
            var unknown = await Send(client, HttpMethod.Post, choicePath, JsonContent.Create(probe with
                { ExpectedRevision = recovered.Revision, Choice = ImportChoiceKind.UseMatch, MatchId = Guid.NewGuid() }), secret);
            Assert.Equal(400, unknown.Status);

            GuidedImportReviewAssertions.VerifyReview(new(offered.JobId, offered.Review!.ItemId, review.ItemId, recovered.Phase.ToString(),
                review.SourceNames.ToArray(), [.. review.Matches.Select(match => match.Title)], offered.Revision, recovered.Revision,
                foreignRead.Status, foreignChoice.Status, Code(stale.Body), Code(unknown.Body)));

            using var wire = new ImportChoiceTransport(CreateClientHandler(certificate));
            using var http = new HttpClient(wire);
            var gateway = new HttpImportIntakeHost(http, new Uri(host.Urls.Single()), library, new TokenProvider(library, instance, secret));
            var routes = new RequestDispatcher.Builder().RegisterQuery<GetHostLibraryState, HostLibraryState>(new AvailableImportLibrary(library));
            var dispatcher = new ImportIntakeHandlers(library, new ImportFilePicker(), gateway).Register(routes).Build();
            using var viewModel = new HostLibraryViewModel(new(dispatcher, new InlineScheduler(), instance), Guid.NewGuid(), library);
            viewModel.Activate(viewModel.Scope.OwnerId);
            await viewModel.OpenLibraryCommand.ExecuteAsync(null);
            await viewModel.InspectJobCommand.ExecuteAsync(offered.JobId);
            var desktopReview = Assert.IsType<ImportReviewItem>(viewModel.Job?.Review);
            Assert.Equal(review.ItemId, desktopReview.ItemId);
            Assert.Equal(review.SourceNames.ToArray(), desktopReview.SourceNames.ToArray());
            Assert.Equal(review.Matches.Select(match => match.MatchId), desktopReview.Matches.Select(match => match.MatchId));

            await ChooseOnDesktop();
            Assert.Null(viewModel.AcceptedJob);
            Assert.Equal(LibraryJobPhase.ReviewRequired, viewModel.Job?.Phase);
            Assert.True(viewModel.Failure?.CanRetry == true);
            await ChooseOnDesktop();
            var receipt = Assert.IsType<LibraryJobReceipt>(viewModel.AcceptedJob);
            Assert.Equal(kind == ImportChoiceKind.Skip ? LibraryJobPhase.Skipped : LibraryJobPhase.Running, viewModel.Job?.Phase);
            Assert.Null(viewModel.Failure);
            Assert.Equal(2, wire.Choices.Count);
            approved = wire.Choices[0];
            Assert.Equal(approved, wire.Choices[1]);
            Assert.Equal(kind, approved.Choice);
            firstReceipt = new(approved.OperationId, receipt.JobId, receipt.LibraryId, receipt.Revision);

            Task ChooseOnDesktop() => kind switch
            {
                ImportChoiceKind.UseMatch => viewModel.ChooseImportMatchCommand.ExecuteAsync(desktopReview.Matches[0].MatchId),
                ImportChoiceKind.KeepTags => viewModel.KeepTagsCommand.ExecuteAsync(null),
                ImportChoiceKind.Skip => viewModel.SkipImportItemCommand.ExecuteAsync(null),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }

        ChooseImportReceipt replay;
        string replayRejection;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var client = CreateClient(host);
            var response = await Send(client, HttpMethod.Post, choicePath, JsonContent.Create(approved), secret);
            Assert.Equal(200, response.Status);
            replay = Deserialize<ChooseImportReceipt>(response.Body);
            var changed = await Send(client, HttpMethod.Post, choicePath,
                JsonContent.Create(approved with { Choice = kind == ImportChoiceKind.Skip ? ImportChoiceKind.KeepTags : ImportChoiceKind.Skip, MatchId = null }), secret);
            Assert.Equal(409, changed.Status);
            replayRejection = Code(changed.Body);
            var competing = await Send(client, HttpMethod.Post, choicePath, JsonContent.Create(approved with { OperationId = Guid.NewGuid() }), secret);
            Assert.Equal(409, competing.Status);
            Assert.Equal("stale-import-review", Code(competing.Body));
        }

        using (var db = new LiteDatabase(database))
        {
            var job = db.GetCollection<BsonDocument>("library_jobs").FindById(offered.JobId);
            var journal = job["ApprovedChoice"].AsDocument;
            Assert.Equal(approved.OperationId, journal["OperationId"].AsGuid);
            Assert.Equal(approved.ReviewItemId, journal["ReviewItemId"].AsGuid);
            Assert.Equal(approved.ExpectedRevision, journal["ReviewRevision"].AsInt64);
            Assert.Equal(firstReceipt, replay);
            var choiceCount = db.GetCollection<BsonDocument>("operation_receipts").FindAll().Count(row =>
                row["Receipt"].AsDocument["Type"].AsString == "ChoseGuidedImport");
            var catalogRevision = db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
            GuidedImportReviewAssertions.VerifyChoice(new(offered.JobId, firstReceipt.JobId, approved.OperationId, replay.OperationId,
                firstReceipt.Revision, replay.Revision, journal["Decision"].AsString, job["Status"].AsString,
                journal["ProviderKey"].IsString ? journal["ProviderKey"].AsString : null, choiceCount, replayRejection, 41, catalogRevision,
                db.GetCollection<BsonDocument>("notification_deliveries").Count(), Directory.EnumerateFileSystemEntries(music).Count()), kind.ToString());
            var staged = Directory.GetFiles(privateStage, "*.flac", SearchOption.AllDirectories);
            Assert.Equal(upload.Files.Length, staged.Length);
            Assert.All(staged, path => Assert.Equal(bytes, File.ReadAllBytes(path)));
        }

        static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
        static string Code(string body) => Deserialize<PlaylistSaveProblem>(body).Code;

        HttpClient CreateClient(WebApplication host) => new(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, database, music, privateStage, Path.Combine(root, "work"), []), identity, credentials, credentials);
        }

        async Task<(int Status, string Body)> Send(HttpClient client, HttpMethod method, string path, HttpContent? content, string credential)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var reply = await client.SendAsync(request, Token);
            return ((int)reply.StatusCode, await reply.Content.ReadAsStringAsync(Token));
        }
    }

    /// <summary>Records the real approval requests and simulates loss after the first committed response.</summary>
    private sealed class ImportChoiceTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<ChooseImportRequest> Choices { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/import-choice", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);
            Choices.Add((await request.Content!.ReadFromJsonAsync<ChooseImportRequest>(cancellationToken))!);
            var response = await base.SendAsync(request, cancellationToken);
            if (Choices.Count != 1 || !response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            throw new HttpRequestException("The fixture discarded a committed choice response.");
        }
    }
}
