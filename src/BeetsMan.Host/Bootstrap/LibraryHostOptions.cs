using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Identifies the locally configured share for one registered device.</summary>
/// <remarks>2026-11-24: Keep separate sender roots for FR-FILESYNC-001#ac-layouts.</remarks>
/// <remarks>2026-11-24: Allow a host-authorized receiver and optional dedicated path before the initial pairing for FR-FILESYNC-001#ac-layouts.</remarks>
public sealed record AndroidHostExport(Guid DeviceId, string FolderId, SyncthingExportSender Sender, Uri? ReceiverEndpoint = null,
    string? ReceiverRoot = null, bool CaseSensitivePaths = false);

/// <summary>Contains the host's typed local paths and registered share routes.</summary>
/// <remarks>2026-11-25: Make host-local YAML profiles selectable for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
public sealed record LibraryHostOptions(Guid LibraryId, string DatabasePath, string MusicRoot, string PrivateStageRoot,
    string PublicationWorkRoot, ImmutableArray<AndroidHostExport> AndroidExports, bool CreateDatabaseIfMissing = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<PcMirrorHostShare> PcMirrors = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BeetsImportOptions? Beets = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> AndroidLayoutFiles = default);

/// <summary>Identifies the host-owned beets executable, catalog and optional native path template.</summary>
/// <remarks>2026-11-24: Enable the native Keep tags and recovery lane for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-25: Provision the separate host-local YAML path for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Allow only host-approved native plugin IDs in desktop configuration edits for FR-BEETS-001#ac-open.</remarks>
public sealed record BeetsImportOptions(string ExecutablePath, string CatalogPath, string? AlbumPathTemplate = null,
    bool CreateCatalogIfMissing = false, BeetsMatchingOptions? Matching = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConfigurationPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<string> AllowedPluginIds = default);

/// <summary>Enables native MusicBrainz matching and identifies the FFmpeg executable that verifies unchanged audio streams.</summary>
/// <remarks>2026-11-24: Enable exact matched album execution for FR-BEETS-001#ac-import and ac-recover.</remarks>
public sealed record BeetsMatchingOptions(string FfmpegExecutablePath, Uri? Endpoint = null);

/// <summary>Supplies a configured sender credential without putting it in host options or shared storage.</summary>
public interface IHostSenderTokenProvider
{
    /// <summary>Reads the host-side Syncthing credential for this library and sender.</summary>
    ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token);
}
