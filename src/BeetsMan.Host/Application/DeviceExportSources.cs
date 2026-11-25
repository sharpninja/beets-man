using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BeetsMan.Host.Application;

/// <summary>Captures the registered Android receiver and the exact export scope it owns.</summary>
/// <remarks>2026-11-24: Bind the registered library, device and share for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed record RegisteredAndroidExport(Guid LibraryId, Guid DeviceId, long Revision, string ShareId,
    string SyncthingDeviceId, Uri ReceiverEndpoint, bool CaseSensitivePaths)
{
    /// <summary>Gets the dedicated receiver path approved by pairing when present.</summary>
    /// <remarks>2026-11-24: Enforce the adopted receiver root for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceiverRoot { get; init; }

    /// <summary>Gets the native sender pinned with the approved receiver folder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovedSenderId { get; init; }

    /// <summary>Gets the stable desktop share identity scoped to this library, device and Syncthing folder.</summary>
    /// <remarks>2026-11-24: Map legacy non-GUID folder IDs without changing share identity during a registry update for FR-FILESYNC-001#ac-layouts.</remarks>
    [JsonIgnore]
    public Guid MediaShareId => new(SHA256.HashData(Encoding.UTF8.GetBytes($"device-export-share/v1/{LibraryId:N}/{DeviceId:N}/{ShareId}")).AsSpan(0, 16));
}

/// <summary>Contains the authoritative catalog information needed to render one destination.</summary>
public sealed record DeviceExportTrackSource(string RelativePath, string? Artist, string? AlbumArtist, string? Album);

/// <summary>Captures one device and its requested catalog tracks within the same host read.</summary>
public sealed record DevicePlaylistSources(RegisteredAndroidExport Destination, ImmutableDictionary<Guid, DeviceExportTrackSource> Tracks);

/// <summary>Reads registered Android destinations and their playlist's library-owned track records.</summary>
public interface IHostDeviceExportSources
{
    /// <summary>Returns a device-scoped snapshot or a rejection when the library or destination differs.</summary>
    Task<HostOutcome<DevicePlaylistSources>> ReadDevicePlaylistSourcesAsync(Guid libraryId, Guid deviceId,
        IEnumerable<Guid> trackIds, CancellationToken token);
}

/// <summary>Reads file presence at exact paths on a registered receiver.</summary>
/// <remarks>2026-11-24: Reuse the authenticated receiver path check for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public interface IHostDeviceExportPresence
{
    /// <summary>Returns a presence flag for every requested path or a dependency outcome.</summary>
    Task<HostOutcome<ImmutableDictionary<string, bool>>> ReadAsync(RegisteredAndroidExport target, IEnumerable<string> paths, CancellationToken token);
}

/// <summary>Reads the saved name needed to render a staged playlist under another profile.</summary>
/// <remarks>2026-11-24: Preserve staged playlist references during STB20 for FR-FILESYNC-001#ac-layouts.</remarks>
public interface IHostExportPlaylistLookup
{
    /// <summary>Returns the saved playlist in exactly the selected library.</summary>
    Task<HostOutcome<SavedHostPlaylist?>> ReadPlaylistAsync(Guid libraryId, Guid playlistId, CancellationToken token);
}
