using System.Collections.Immutable;
using BeetsMan.Host.Application;
using LiteDB;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Supplies the active Android registration boundaries for the event worker and authenticated readers.</summary>
/// <remarks>2026-11-24: Exclude revoked, PC and other-library identities from FR-FILESYNC-001#ac-arrival.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    /// <summary>Reads the current Android bindings for the locally configured library.</summary>
    internal Task<HostOutcome<ImmutableArray<RegisteredAndroidExport>>> ReadAndroidArrivalsAsync(Guid libraryId, AuthenticatedHostInstance? caller,
        CancellationToken token) => OnLaneAsync<ImmutableArray<RegisteredAndroidExport>>(() =>
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        if (caller is not null && (!IsPublicationCaller(libraryId, caller) || Text(instances.FindById(caller.InstanceId), "Role") != "Desktop"))
            return new HostOutcome<ImmutableArray<RegisteredAndroidExport>>.Forbidden();
        var bindings = instances.Find(Query.EQ("LibraryId", libraryId)).Where(row => row["_id"].IsGuid && Text(row, "Role") == "Android")
            .Select(row => ReadRegisteredAndroidExport(libraryId, row["_id"].AsGuid)).OfType<RegisteredAndroidExport>().OrderBy(item => item.DeviceId);
        return new HostOutcome<ImmutableArray<RegisteredAndroidExport>>.Success([.. bindings]);
    }, token);
}
