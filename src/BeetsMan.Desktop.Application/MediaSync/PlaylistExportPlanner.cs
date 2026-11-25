using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Supplies the authoritative host operation that stages a playlist in its planned device files.</summary>
public interface IPlaylistExportHost
{
    /// <summary>Submits one acknowledged, revision-checked staging operation.</summary>
    Task<Outcome<DeviceExportPlan>> StagePlaylistAsync(StagePlaylistForDevice request, CommandContext context, CancellationToken cancellationToken);
}

/// <summary>Forwards the typed desktop intent to the authorized export owner.</summary>
public sealed class StagePlaylistForDeviceHandler(IPlaylistExportHost host) : ICommandHandler<StagePlaylistForDevice, DeviceExportPlan>
{
    /// <inheritdoc />
    public Task<Outcome<DeviceExportPlan>> HandleAsync(StagePlaylistForDevice command, CommandContext context, CancellationToken cancellationToken) =>
        host.StagePlaylistAsync(command, context, cancellationToken);
}
