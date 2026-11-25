using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Submits edits to the authoritative staged list for one Android export.</summary>
/// <remarks>2026-11-24: Bind the existing STB10 commands for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public interface IDeviceSyncListEditHost
{
    /// <summary>Stages the selected tracks and returns the revised plan.</summary>
    Task<Outcome<DeviceExportPlan>> StageTracksAsync(StageTracksForDevice command, CommandContext context, CancellationToken token);

    /// <summary>Applies a staged-intent edit and returns the revised plan.</summary>
    Task<Outcome<DeviceExportPlan>> EditPlanAsync(EditDeviceExportPlan command, CommandContext context, CancellationToken token);
}

/// <summary>Registers the existing typed staged-list commands with the desktop dispatcher.</summary>
public sealed class DeviceSyncListEditHandlers(IDeviceSyncListEditHost host) :
    ICommandHandler<StageTracksForDevice, DeviceExportPlan>, ICommandHandler<EditDeviceExportPlan, DeviceExportPlan>
{
    /// <summary>Registers both plan edit handlers.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterCommand<StageTracksForDevice, DeviceExportPlan>(this)
        .RegisterCommand<EditDeviceExportPlan, DeviceExportPlan>(this);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> HandleAsync(StageTracksForDevice command, CommandContext context, CancellationToken token) =>
        host.StageTracksAsync(command, context, token);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> HandleAsync(EditDeviceExportPlan command, CommandContext context, CancellationToken token) =>
        host.EditPlanAsync(command, context, token);
}
