using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Reads registered Android layout and staged-plan projections from the host.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-layouts and ac-playlist-drop to typed application queries.</remarks>
public interface IDeviceExportReadHost
{
    /// <summary>Returns the device's available profiles.</summary>
    Task<Outcome<DeviceLayoutsSnapshot>> ReadLayoutsAsync(GetDeviceLayouts query, RequestContext context, CancellationToken token);

    /// <summary>Returns the selected profile's current or named stage list.</summary>
    Task<Outcome<DeviceExportPlan>> ReadPlanAsync(GetDeviceExportPlan query, RequestContext context, CancellationToken token);
}

/// <summary>Dispatches device layout and plan lookups to the configured host.</summary>
public sealed class DeviceExportQueryHandlers(IDeviceExportReadHost host) : IQueryHandler<GetDeviceLayouts, DeviceLayoutsSnapshot>,
    IQueryHandler<GetDeviceExportPlan, DeviceExportPlan>
{
    /// <inheritdoc />
    public Task<Outcome<DeviceLayoutsSnapshot>> HandleAsync(GetDeviceLayouts query, RequestContext context, CancellationToken cancellationToken) =>
        host.ReadLayoutsAsync(query, context, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> HandleAsync(GetDeviceExportPlan query, RequestContext context, CancellationToken cancellationToken) =>
        host.ReadPlanAsync(query, context, cancellationToken);
}
