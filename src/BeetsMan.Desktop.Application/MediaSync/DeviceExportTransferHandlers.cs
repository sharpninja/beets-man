using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Provides scoped export transfer reads and explicit retry from the registered host.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-safety to the existing transfer CQRS commands.</remarks>
public interface IDeviceExportTransferHost
{
    /// <summary>Reads the durable receiver progress for this transfer.</summary>
    Task<Outcome<MediaTransferState>> ReadTransferAsync(GetMediaTransfer query, RequestContext context, CancellationToken token);

    /// <summary>Resumes the specified transfer revision with the caller's durable operation identity.</summary>
    Task<Outcome<MediaTransferState>> RetryTransferAsync(ResumeMediaTransfer command, CommandContext context, CancellationToken token);
}

/// <summary>Registers the existing inspection and retry commands with desktop CQRS.</summary>
public sealed class DeviceExportTransferHandlers(IDeviceExportTransferHost host) : IQueryHandler<GetMediaTransfer, MediaTransferState>,
    ICommandHandler<ResumeMediaTransfer, MediaTransferState>
{
    /// <summary>Registers both transfer routes with the desktop request dispatcher.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetMediaTransfer, MediaTransferState>(this).RegisterCommand<ResumeMediaTransfer, MediaTransferState>(this);

    /// <inheritdoc />
    public Task<Outcome<MediaTransferState>> HandleAsync(GetMediaTransfer query, RequestContext context, CancellationToken cancellationToken) =>
        host.ReadTransferAsync(query, context, cancellationToken);

    /// <inheritdoc />
    public Task<Outcome<MediaTransferState>> HandleAsync(ResumeMediaTransfer command, CommandContext context, CancellationToken cancellationToken) =>
        host.RetryTransferAsync(command, context, cancellationToken);
}
