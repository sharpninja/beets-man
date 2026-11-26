using BeetsMan.Desktop.Adapters.Persistence;
using LiteDB;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies the schema identity written by first-run desktop store creation.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 to TR-RECORDS-ARCH-001#ac-litedb-c4-entities.</remarks>
public sealed class LiteDbDesktopStoreSchemaIntegrationTests
{
    /// <summary>Checks that schema version one and the original desktop identity survive a reopen.</summary>
    [Fact]
    public void NewStorePersistsSchemaVersionOne()
    {
        var root = Directory.CreateTempSubdirectory("beets-desktop-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "desktop.db");
            Guid storeId;
            using (var created = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
                storeId = created.StoreId;

            using (var database = new LiteDatabase(path))
            {
                var stored = Assert.Single(database.GetCollection<BsonDocument>("store_schema").FindAll());
                Assert.Equal("store", stored["_id"].AsString);
                Assert.Equal("Desktop", stored["StoreRole"].AsString);
                Assert.True(stored["SchemaVersion"].IsInt32);
                Assert.Equal(1, stored["SchemaVersion"].AsInt32);
                Assert.Equal(storeId, stored["StoreId"].AsGuid);
                Assert.NotEqual(Guid.Empty, storeId);
            }

            using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
            Assert.Equal(storeId, reopened.StoreId);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
