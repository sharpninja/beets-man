using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;

namespace BeetsMan.Desktop.Bootstrap;

public sealed partial class DesktopStartup
{
    /// <summary>Binds the registered scope's Sync queries and commands to the same authenticated host adapter.</summary>
    /// <remarks>2026-11-25: Compose FR-FILESYNC-001#ac-mirror, ac-layouts, ac-safety and ac-playlist-drop before binding screens.</remarks>
    private IAndroidArrivalHost? RegisterMediaSync(RequestDispatcher.Builder routes)
    {
        if (host is not { } registered)
            return null;
        var media = new HttpDevicePlaylistExportHost(registered.Client, registered.Endpoint, library, registered.Credentials);
        var queries = new DeviceExportQueryHandlers(media);
        routes.RegisterQuery<GetDeviceLayouts, DeviceLayoutsSnapshot>(queries)
            .RegisterQuery<GetDeviceExportPlan, DeviceExportPlan>(queries)
            .RegisterCommand<StagePlaylistForDevice, DeviceExportPlan>(new StagePlaylistForDeviceHandler(media));
        new DeviceSyncListEditHandlers(media).Register(routes);
        new DeviceExportCommandHandlers(media).Register(routes);
        new DeviceExportTransferHandlers(media).Register(routes);
        new MediaMirrorHandlers(media).Register(routes);
        return media;
    }
}
