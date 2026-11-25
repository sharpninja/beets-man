using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Accesses the authenticated host's registered media directory and PC mirror controls.</summary>
/// <remarks>2026-11-24: Connect STB10 status, pause, resume and rescan for FR-FILESYNC-001#ac-mirror.</remarks>
/// <remarks>2026-11-24: Dispatch pairing approvals for FR-FILESYNC-001#ac-mirror and ac-layouts.</remarks>
public interface IMediaMirrorHost
{
    /// <summary>Reads the authorized peers and share snapshots.</summary>
    Task<Outcome<MediaSyncState>> ReadMediaAsync(RequestContext context, CancellationToken token);

    /// <summary>Submits the approved registered peer and destination at the current directory revision.</summary>
    Task<Outcome<MediaSyncState>> PairMediaPeerAsync(PairMediaPeer command, CommandContext context, CancellationToken token);

    /// <summary>Changes the selected mirror's pause state at its expected revision.</summary>
    Task<Outcome<MediaSyncState>> SetMirrorPausedAsync(SetMirrorPaused command, CommandContext context, CancellationToken token);

    /// <summary>Rescans only the requested registered share.</summary>
    Task<Outcome<MediaSyncState>> RescanMirrorAsync(RescanMediaShare command, CommandContext context, CancellationToken token);
}

/// <summary>Registers existing mirror commands with the application CQRS dispatcher.</summary>
public sealed class MediaMirrorHandlers(IMediaMirrorHost host) : IQueryHandler<GetMediaSyncState, MediaSyncState>,
    ICommandHandler<PairMediaPeer, MediaSyncState>, ICommandHandler<SetMirrorPaused, MediaSyncState>, ICommandHandler<RescanMediaShare, MediaSyncState>
{
    /// <summary>Registers the status query, pairing, and mirror control commands.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder.RegisterQuery<GetMediaSyncState, MediaSyncState>(this)
        .RegisterCommand<PairMediaPeer, MediaSyncState>(this).RegisterCommand<SetMirrorPaused, MediaSyncState>(this)
        .RegisterCommand<RescanMediaShare, MediaSyncState>(this);

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> HandleAsync(GetMediaSyncState query, RequestContext context, CancellationToken token) =>
        host.ReadMediaAsync(context, token);

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> HandleAsync(PairMediaPeer command, CommandContext context, CancellationToken token) =>
        host.PairMediaPeerAsync(command, context, token);

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> HandleAsync(SetMirrorPaused command, CommandContext context, CancellationToken token) =>
        host.SetMirrorPausedAsync(command, context, token);

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> HandleAsync(RescanMediaShare command, CommandContext context, CancellationToken token) =>
        host.RescanMirrorAsync(command, context, token);
}
