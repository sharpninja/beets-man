using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Submits preview and publish commands at the authorized library host.</summary>
/// <remarks>2026-11-24: Forward the CQRS operation identity for FR-FILESYNC-001#ac-safety.</remarks>
public interface IDeviceExportCommandHost
{
    /// <summary>Prepares the requested staged revision and returns the reviewed changes.</summary>
    Task<Outcome<DeviceExportPreview>> PreparePreviewAsync(PrepareDeviceExportPreview command, CommandContext context, CancellationToken token);

    /// <summary>Applies the reviewed revision or returns the matching confirmation proposal.</summary>
    Task<Outcome<DeviceExportSubmission>> PublishAsync(PublishDeviceExport command, CommandContext context, CancellationToken token);
}

/// <summary>Registers the existing typed export commands with the desktop CQRS dispatcher.</summary>
public sealed class DeviceExportCommandHandlers(IDeviceExportCommandHost host) : ICommandHandler<PrepareDeviceExportPreview, DeviceExportPreview>,
    ICommandHandler<PublishDeviceExport, DeviceExportSubmission>
{
    /// <summary>Registers both mutation results with a normal desktop dispatcher.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterCommand<PrepareDeviceExportPreview, DeviceExportPreview>(this)
        .RegisterCommand<PublishDeviceExport, DeviceExportSubmission>(this);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportPreview>> HandleAsync(PrepareDeviceExportPreview command, CommandContext context, CancellationToken cancellationToken) =>
        host.PreparePreviewAsync(command, context, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<DeviceExportSubmission>> HandleAsync(PublishDeviceExport command, CommandContext context, CancellationToken cancellationToken) =>
        host.PublishAsync(command, context, cancellationToken);
}
