using BeetsMan.Host.Adapters.Persistence;
using LiteDB;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Checks that the host preserves stores using a newer, unsupported schema.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 to TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
public sealed class LiteDbHostStoreSchemaIntegrationTests
{
    /// <summary>Rejects a schema-two store and verifies the original identity, version, and catalog row remain.</summary>
    [Fact]
    public void UnsupportedNewerSchemaIsRejectedAndPreserved()
    {
        var root = Directory.CreateTempSubdirectory("beets-host-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "host.db");
            using (LiteDbHostPlaylistStore.Open(path, allowCreate: true)) { }
            Guid storeId;
            var artist = new BsonDocument
            {
                ["_id"] = Guid.NewGuid(), ["LibraryId"] = Guid.NewGuid(), ["Revision"] = 1L,
                ["Name"] = "Lena Vale", ["SortName"] = "Vale, Lena"
            };
            using (var database = new LiteDatabase(path))
            {
                var schemas = database.GetCollection<BsonDocument>("store_schema");
                var schema = Assert.Single(schemas.FindAll());
                storeId = schema["StoreId"].AsGuid;
                Assert.Equal(1, schema["SchemaVersion"].AsInt32);
                schema["SchemaVersion"] = 2;
                Assert.True(schemas.Update(schema));
                database.GetCollection<BsonDocument>("catalog_artists").Insert(artist);
            }

            var failure = Assert.Throws<InvalidDataException>(() =>
            {
                using var unexpected = LiteDbHostPlaylistStore.Open(path, allowCreate: false);
            });
            Assert.Contains("schema version is not supported", failure.Message, StringComparison.Ordinal);

            using var preserved = new LiteDatabase(path);
            var unchanged = Assert.Single(preserved.GetCollection<BsonDocument>("store_schema").FindAll());
            Assert.Equal("store", unchanged["_id"].AsString);
            Assert.Equal("Host", unchanged["StoreRole"].AsString);
            Assert.Equal(2, unchanged["SchemaVersion"].AsInt32);
            Assert.Equal(storeId, unchanged["StoreId"].AsGuid);
            var retained = Assert.Single(preserved.GetCollection<BsonDocument>("catalog_artists").FindAll());
            Assert.Equal(artist.ToString(), retained.ToString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
