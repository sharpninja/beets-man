using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies the real catalog transaction and replay with complete and partial reconciled album fixtures.</summary>
    /// <remarks>2026-11-24: Bind FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate to the host catalog, feed and delivery records.</remarks>
    [Fact]
    public async Task GuidedImportPublicationCommitsCatalogJobAndDeliveryTogether()
    {
        const string firstPath = "Fixture Band/Fixture Album/01 North.flac";
        const string secondPath = "Fixture Band/Fixture Album/02 South.flac";
        const string occupiedPath = "Unrelated/Occupied.flac";
        var database = Path.Combine(root, "publication.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var stage = Path.Combine(root, "stage");
        var existingTrack = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        SeedHost(database, revokedInstance: Guid.NewGuid());
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = recipient, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = Guid.NewGuid(), ["LibraryId"] = Guid.NewGuid(), ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 41L });
            var tracks = db.GetCollection<BsonDocument>("catalog_tracks");
            tracks.Insert(new BsonDocument { ["_id"] = existingTrack, ["LibraryId"] = library, ["BeetsTrackId"] = 27L,
                ["Title"] = "Existing North", ["Rating"] = 4, ["RelativePath"] = firstPath, ["Revision"] = 40L });
            tracks.Insert(new BsonDocument { ["_id"] = Guid.NewGuid(), ["LibraryId"] = library, ["BeetsTrackId"] = 19L,
                ["Title"] = "Unrelated", ["RelativePath"] = occupiedPath, ["Revision"] = 40L });
        }

        var originalRevision = Revision();
        var identity = new AuthenticatedHostInstance(library, instance);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        using var certificate = CreateCertificate();
        var uploadId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        ReconciledImportAlbum reconciled;
        string partialCode, wrongOperationCode;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var sourceBytes = Encoding.UTF8.GetBytes("fixture input");
            var digest = Convert.ToHexString(SHA256.HashData(sourceBytes));
            var intake = host.Services.GetRequiredService<HostImportIntake>();
            Assert.IsType<HostOutcome<ImportUploadReceipt>.Success>(await intake.BeginAsync(library, identity, new(instance, uploadId,
                [new("North.flac", sourceBytes.Length, digest), new("South.flac", sourceBytes.Length, digest)]), Token));
            for (var index = 0; index < 2; index++)
            {
                using var stream = new MemoryStream(sourceBytes);
                Assert.IsType<HostOutcome<ImportUploadPartReceipt>.Success>(await intake.PutPartAsync(library, identity, uploadId,
                    index, sourceBytes.Length, stream, _ => { }, Token));
            }
            var accepted = Assert.IsType<HostOutcome<StartImportReceipt>.Success>(await intake.StartAsync(library, identity,
                new(instance, Guid.NewGuid(), uploadId), Token)).Value;
            var review = host.Services.GetRequiredService<HostImportReview>();
            var offered = Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(await review.OfferAsync(library, accepted.JobId,
                uploadId, [], Token)).Value;
            var choice = Assert.IsType<HostOutcome<ChooseImportReceipt>.Success>(await review.ChooseAsync(library, accepted.JobId,
                identity, new(instance, choiceId, offered.Review!.ItemId, offered.Revision, ImportChoiceKind.KeepTags), Token)).Value;

            reconciled = new(library, accepted.JobId, uploadId, choiceId, choice.Revision, null, 11L, "Fixture Album", "Fixture Band",
                [new(0, 27, firstPath, "North", "Fixture Band", 1, 42_000_000, "Ambient"),
                 new(1, 28, secondPath, "South", "Fixture Band", 2, 43_000_000, "Ambient")]);
            var publication = host.Services.GetRequiredService<ImportCatalogPublication>();
            partialCode = Assert.IsType<HostOutcome<ImportJobSnapshot>.Rejected>(await publication.PublishAsync(
                reconciled with { Tracks = [reconciled.Tracks[0]] }, Token)).Code;
            wrongOperationCode = Assert.IsType<HostOutcome<ImportJobSnapshot>.Rejected>(await publication.PublishAsync(
                reconciled with { ChoiceOperationId = Guid.NewGuid() }, Token)).Code;
            var paths = reconciled.Tracks.SetItem(1, reconciled.Tracks[1] with { RelativePath = occupiedPath });
            var collision = Assert.IsType<HostOutcome<ImportJobSnapshot>.Rejected>(await publication.PublishAsync(reconciled with { Tracks = paths }, Token));
            Assert.Equal("import-catalog-identity-conflict", collision.Code);
        }

        var revisionAfterPartial = Revision();
        using (var db = new LiteDatabase(database))
        {
            Assert.Equal("Existing North", db.GetCollection<BsonDocument>("catalog_tracks").FindById(existingTrack)["Title"].AsString);
            Assert.Equal(0, db.GetCollection<BsonDocument>("catalog_artists").Count());
            Assert.Equal(0, db.GetCollection<BsonDocument>("catalog_albums").Count());
            Assert.Equal(0, db.GetCollection<BsonDocument>("revision_changes").Count());
            Assert.Equal(0, db.GetCollection<BsonDocument>("notification_deliveries").Count());
        }

        ImportJobSnapshot committed;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var publication = host.Services.GetRequiredService<ImportCatalogPublication>();
            var competingRetries = await Task.WhenAll(publication.PublishAsync(reconciled, Token), publication.PublishAsync(reconciled, Token));
            committed = Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(competingRetries[0]).Value;
            Assert.Equal(committed, Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(competingRetries[1]).Value);
        }

        ImportJobSnapshot replay, readback;
        string changedReplayCode;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            var publication = host.Services.GetRequiredService<ImportCatalogPublication>();
            replay = Assert.IsType<HostOutcome<ImportJobSnapshot>.Success>(await publication.PublishAsync(
                reconciled with { Tracks = [.. reconciled.Tracks.Reverse()] }, Token)).Value;
            changedReplayCode = Assert.IsType<HostOutcome<ImportJobSnapshot>.Rejected>(await publication.PublishAsync(
                reconciled with { Title = "Altered replay" }, Token)).Code;
            using var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            readback = (await client.GetFromJsonAsync<ImportJobSnapshot>(ImportIntakeRoutes.JobPath(library, reconciled.JobId), Token))!;
            Assert.Equal(committed, readback);
        }

        using (var db = new LiteDatabase(database))
        {
            var changes = db.GetCollection<BsonDocument>("revision_changes").FindAll().ToArray();
            var feed = Assert.Single(changes);
            var deliveries = db.GetCollection<BsonDocument>("notification_deliveries").FindAll().ToArray();
            foreach (var delivery in deliveries)
                Assert.Equal(committed.CatalogRevision, delivery["Revision"].AsInt64);
            var rows = db.GetCollection<BsonDocument>("catalog_tracks").FindAll().Where(row => row["Revision"].AsInt64 == 42L)
                .OrderBy(row => row["BeetsTrackId"].AsInt64).ToArray();
            var album = Assert.Single(db.GetCollection<BsonDocument>("catalog_albums").FindAll());
            var artist = Assert.Single(db.GetCollection<BsonDocument>("catalog_artists").FindAll());
            Assert.All(rows, row => { Assert.Equal(album["_id"], row["AlbumId"]); Assert.Equal(artist["_id"], row["ArtistId"]); });
            Assert.Equal(4, rows[0]["Rating"].AsInt32);
            ImportPublicationAssertions.Verify(new(originalRevision, revisionAfterPartial, committed.CatalogRevision!.Value, replay.CatalogRevision!.Value,
                partialCode, wrongOperationCode, changedReplayCode, readback.Phase.ToString(), readback.Revision, existingTrack, rows[0]["_id"].AsGuid,
                [.. rows.Select(row => row["BeetsTrackId"].AsInt64)], [.. rows.Select(row => row["RelativePath"].AsString)],
                [.. feed["Changes"].AsArray.Select(change => change.AsDocument["Kind"].AsString)], choiceId, feed["OperationId"].AsGuid,
                recipient, [.. deliveries.Select(row => row["RecipientInstanceId"].AsGuid)], changes.Length));
        }

        long Revision()
        {
            using var db = new LiteDatabase(database);
            return db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
        }

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, database, music, stage, Path.Combine(root, "work"), []),
                new Verifier(identity, secret, null), credentials, credentials);
        }
    }
}
