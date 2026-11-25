using BeetsMan.Host.Application;
using LiteDB;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Seeds the Android half of a registered export independently of the host resolver.</summary>
/// <remarks>2026-11-24: Bind TEST-FILESYNC-002 to real registration scope for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
internal static class TestAndroidRegistration
{
    public static void Seed(ILiteDatabase database, RegisteredAndroidExport receiver) =>
        database.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
        {
            ["_id"] = receiver.DeviceId, ["LibraryId"] = receiver.LibraryId, ["Role"] = "Android", ["Status"] = "Active",
            ["SyncthingDeviceId"] = receiver.SyncthingDeviceId,
            ["AndroidExport"] = new BsonDocument
            {
                ["Revision"] = receiver.Revision, ["ShareId"] = receiver.ShareId, ["ReceiverEndpoint"] = receiver.ReceiverEndpoint.AbsoluteUri,
                ["CaseSensitivePaths"] = receiver.CaseSensitivePaths
            }
        });
}
