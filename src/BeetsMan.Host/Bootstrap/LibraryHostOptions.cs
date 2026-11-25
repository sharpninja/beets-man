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
public sealed record LibraryHostOptions(Guid LibraryId, string DatabasePath, string MusicRoot, string PrivateStageRoot,
    string PublicationWorkRoot, ImmutableArray<AndroidHostExport> AndroidExports, bool CreateDatabaseIfMissing = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ImmutableArray<PcMirrorHostShare> PcMirrors = default);

/// <summary>Supplies a configured sender credential without putting it in host options or shared storage.</summary>
public interface IHostSenderTokenProvider
{
    /// <summary>Reads the host-side Syncthing credential for this library and sender.</summary>
    ValueTask<string?> GetTokenAsync(Guid libraryId, string senderDeviceId, CancellationToken token);
}
