using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Routes snapshot reads, file publication and sender scans to the same configured Android share.</summary>
/// <remarks>2026-11-24: Bind the active registered share to its local root for FR-FILESYNC-001#ac-safety.</remarks>
internal sealed class ConfiguredAndroidExportRoutes(LibraryHostOptions options, IDeviceExportPayloadStore payloads, IHostSenderTokenProvider credentials)
    : IDeviceExportShareReader, IDeviceExportStepWriter, ISyncthingExportSenderRegistry
{
    private readonly Dictionary<Guid, AndroidHostExport> routes = options.AndroidExports.ToDictionary(route => route.DeviceId);

    /// <inheritdoc />
    /// <remarks>2026-11-24: Preserve the pairing sender approval for FR-FILESYNC-001#ac-safety.</remarks>
    public SyncthingExportSender? Find(RegisteredAndroidExport target) => target.LibraryId == options.LibraryId &&
        routes.TryGetValue(target.DeviceId, out var route) && route.FolderId == target.ShareId &&
        (target.ApprovedSenderId is null || route.Sender.DeviceId == target.ApprovedSenderId) ? route.Sender : null;

    /// <inheritdoc />
    public ValueTask<string?> GetTokenAsync(RegisteredAndroidExport target, CancellationToken token) => Find(target) is { } sender
        ? credentials.GetTokenAsync(options.LibraryId, sender.DeviceId, token) : ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportShareSnapshot>> ReadAsync(RegisteredAndroidExport target, IEnumerable<string> paths, CancellationToken token) =>
        Find(target) is { } route ? new FileDeviceExportSnapshotReader(route.ExportRoot, target).ReadAsync(target, paths, token) :
            Task.FromResult<HostOutcome<DeviceExportShareSnapshot>>(new HostOutcome<DeviceExportShareSnapshot>.Rejected("export-share-unconfigured"));

    /// <inheritdoc />
    public Task<HostOutcome<bool>> ApplyAsync(PendingDeviceExportPublication work, int ordinal, CancellationToken token) =>
        Find(work.Destination) is { } route ? new FileDeviceExportStepWriter(route.ExportRoot, options.PublicationWorkRoot, work.Destination, payloads)
            .ApplyAsync(work, ordinal, token) : Task.FromResult<HostOutcome<bool>>(new HostOutcome<bool>.Rejected("export-share-unconfigured"));
}
