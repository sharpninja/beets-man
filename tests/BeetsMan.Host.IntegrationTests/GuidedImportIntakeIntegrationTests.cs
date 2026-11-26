using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// <summary>Verifies ordered private copies and replayable job acceptance through the actual HTTPS host and LiteDB store.</summary>
    /// <remarks>2026-11-24: Bind the approved STB08 intake observations to FR-BEETS-001#ac-import, ac-recover, and TEST-BEETS-002.</remarks>
    [Fact]
    public async Task GuidedImportStagesVerifiedCopiesBeforeAcceptingExactlyOneJob()
    {
        var database = Path.Combine(root, "host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "selected-sources")).FullName;
        var privateStage = Path.Combine(root, "private");
        var other = Guid.NewGuid();
        SeedHost(database);
        using (var db = new LiteDatabase(database))
        {
            db.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
            { ["_id"] = other, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
            db.GetCollection<BsonDocument>("revision_heads").Insert(new BsonDocument { ["_id"] = library, ["CurrentRevision"] = 17L });
        }
        var initialCatalogRevision = CatalogRevision();
        var first = Path.Combine(source, "Side A.flac");
        var second = Path.Combine(source, "Side B.flac");
        await File.WriteAllTextAsync(first, "fixture audio A", Token);
        await File.WriteAllTextAsync(second, "fixture audio B", Token);
        var firstBytes = await File.ReadAllBytesAsync(first, Token);
        var secondBytes = await File.ReadAllBytesAsync(second, Token);
        var manifest = new BeginImportUploadRequest(instance, Guid.NewGuid(), [Describe(first, firstBytes), Describe(second, secondBytes)]);
        var operation = new StartImportRequest(instance, Guid.NewGuid(), manifest.UploadId);
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var identity = new Verifier(new(library, instance), secret, other);
        using var certificate = CreateCertificate();
        int withoutToken, foreignLibrary, otherOwner;
        string corruptCode, incompleteCode, changedCode;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            withoutToken = (await Send(client, HttpMethod.Post, ImportIntakeRoutes.BeginPath(library), JsonContent.Create(manifest), null)).Status;
            foreignLibrary = (await Send(client, HttpMethod.Post, ImportIntakeRoutes.BeginPath(Guid.NewGuid()), JsonContent.Create(manifest), secret)).Status;
            await Begin(client, manifest);
            otherOwner = (await Copy(client, 0, firstBytes, RevokedCredential)).Status;
            var corrupt = await Copy(client, 0, Encoding.UTF8.GetBytes("fixture audio C"), secret);
            Assert.Equal(400, corrupt.Status);
            corruptCode = Code(corrupt.Body);
            Assert.Equal(200, (await Copy(client, 0, firstBytes, secret)).Status);
            var incomplete = await Start(client, operation);
            Assert.Equal(400, incomplete.Status);
            incompleteCode = Code(incomplete.Body);
            var changed = await Send(client, HttpMethod.Post, ImportIntakeRoutes.BeginPath(library),
                JsonContent.Create(manifest with { Files = manifest.Files.Reverse().ToImmutableArray() }), secret);
            Assert.Equal(409, changed.Status);
            changedCode = Code(changed.Body);
        }
        var jobsBefore = Jobs();
        Assert.Single(Directory.EnumerateFiles(privateStage, "*", SearchOption.AllDirectories));

        StartImportReceipt firstAccepted;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var wire = new LostResponseHandler(CreateClientHandler(certificate));
            using var client = new HttpClient(wire) { BaseAddress = new(host.Urls.Single()) };
            await Begin(client, manifest);
            Assert.Equal(200, (await Copy(client, 0, firstBytes, secret)).Status);
            Assert.Equal(200, (await Copy(client, 1, secondBytes, secret)).Status);
            wire.DropNextSuccess = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => Start(client, operation));
            var response = await Start(client, operation);
            Assert.Equal(200, response.Status);
            firstAccepted = Deserialize<StartImportReceipt>(response.Body);
            Assert.Equal(operation.OperationId, firstAccepted.OperationId);
        }

        ImportJobSnapshot recovered;
        await using (var host = BuildHost())
        {
            await host.StartAsync(Token);
            using var client = Client(host);
            var replay = await Start(client, operation);
            Assert.Equal(200, replay.Status);
            Assert.Equal(firstAccepted, Deserialize<StartImportReceipt>(replay.Body));
            var anotherOperation = await Start(client, operation with { OperationId = Guid.NewGuid() });
            Assert.Equal(firstAccepted.JobId, Deserialize<StartImportReceipt>(anotherOperation.Body).JobId);
            var statusPath = ImportIntakeRoutes.JobPath(library, firstAccepted.JobId);
            Assert.Equal(404, (await Send(client, HttpMethod.Get, statusPath, null, RevokedCredential)).Status);
            var status = await Send(client, HttpMethod.Get, statusPath, null, secret);
            Assert.Equal(200, status.Status);
            Assert.True(status.NoStore);
            recovered = Deserialize<ImportJobSnapshot>(status.Body);
        }

        var staged = Directory.EnumerateFiles(privateStage, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, Directory.EnumerateFiles(privateStage, "*", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFileSystemEntries(music));
        Assert.All(staged, path => Assert.StartsWith(Path.Combine(privateStage, "imports") + Path.DirectorySeparatorChar, path, StringComparison.Ordinal));
        string[] manifestNames;
        using (var db = new LiteDatabase(database))
        {
            var persisted = db.GetCollection<BsonDocument>("import_uploads").FindAll().Single();
            manifestNames = [.. Deserialize<ImportUploadFile[]>(persisted["Files"].AsString).Select(file => file.Name)];
            Assert.Equal(manifest.UploadId, persisted["UploadId"].AsGuid);
            Assert.Equal(firstAccepted.JobId, persisted["JobId"].AsGuid);
        }

        ImportUploadAssertions.Verify(new(withoutToken, foreignLibrary, otherOwner, corruptCode, incompleteCode, changedCode, manifestNames,
            [await File.ReadAllTextAsync(first, Token), await File.ReadAllTextAsync(second, Token)],
            [.. await Task.WhenAll(staged.Select(path => File.ReadAllTextAsync(path, Token)))], jobsBefore, Jobs(), firstAccepted.JobId, recovered.JobId,
            recovered.Phase.ToString(), initialCatalogRevision, CatalogRevision()));

        static ImportUploadFile Describe(string path, byte[] content) => new(Path.GetFileName(path), content.LongLength, Convert.ToHexString(SHA256.HashData(content)));
        static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, JsonSerializerOptions.Web)!;
        static string Code(string body) => Deserialize<PlaylistSaveProblem>(body).Code;

        HttpClient Client(WebApplication host) => new(CreateClientHandler(certificate)) { BaseAddress = new(host.Urls.Single()) };

        WebApplication BuildHost()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            return LibraryHostStartup.Build(builder, new(library, database, music, privateStage, Path.Combine(root, "work"), []), identity, credentials, credentials);
        }

        async Task Begin(HttpClient client, BeginImportUploadRequest request)
        {
            var response = await Send(client, HttpMethod.Post, ImportIntakeRoutes.BeginPath(library), JsonContent.Create(request), secret);
            Assert.Equal(200, response.Status);
            Assert.Equal(request.UploadId, Deserialize<ImportUploadReceipt>(response.Body).UploadId);
        }

        Task<(int Status, string Body, bool NoStore)> Copy(HttpClient client, int index, byte[] bytes, string credential) =>
            Send(client, HttpMethod.Put, ImportIntakeRoutes.PartPath(library, manifest.UploadId, index), new ByteArrayContent(bytes), credential);

        Task<(int Status, string Body, bool NoStore)> Start(HttpClient client, StartImportRequest request) =>
            Send(client, HttpMethod.Post, ImportIntakeRoutes.StartPath(library), JsonContent.Create(request), secret);

        async Task<(int Status, string Body, bool NoStore)> Send(HttpClient client, HttpMethod method, string path, HttpContent? content, string? credential)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            if (credential is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var reply = await client.SendAsync(request, Token);
            return ((int)reply.StatusCode, await reply.Content.ReadAsStringAsync(Token), reply.Headers.CacheControl?.NoStore == true);
        }

        int Jobs()
        {
            using var db = new LiteDatabase(database);
            return db.GetCollection<BsonDocument>("library_jobs").Count();
        }

        long CatalogRevision()
        {
            using var db = new LiteDatabase(database);
            return db.GetCollection<BsonDocument>("revision_heads").FindById(library)["CurrentRevision"].AsInt64;
        }
    }
}
