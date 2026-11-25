using System.Net.Http.Json;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Projects registered mirror observations and receipt-scoped controls through the authenticated host client.</summary>
/// <remarks>2026-11-24: Bind FR-FILESYNC-001#ac-mirror without bringing native credentials into the desktop.</remarks>
/// <remarks>2026-11-24: Validate pairing result scope and revision for FR-FILESYNC-001#ac-mirror and ac-layouts.</remarks>
public sealed partial class HttpDevicePlaylistExportHost
{
    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> ReadMediaAsync(RequestContext context, CancellationToken token)
    {
        if (context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<MediaSyncState>("media-mirror-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.MediaMirrorRoutes.ReadPath);
        return SendAsync<Wire.MediaSyncSnapshot, MediaSyncState>(new(HttpMethod.Get, route), origin, "media-mirror-read",
            "media-mirror-response-mismatch", MapMedia, token);
    }

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> PairMediaPeerAsync(PairMediaPeer command, CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<MediaSyncState>("media-pairing-origin-required"));
        if (command.ShareSelection is not { } selection || !Enum.IsDefined(selection.Kind) ||
            command.PeerId == Guid.Empty || command.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(selection.LocalRoot))
            return Task.FromResult(Invalid<MediaSyncState>("invalid-media-pairing"));
        var kind = selection.Kind == MediaPeerKind.PcMirror ? Wire.MediaPeerType.PcMirror : Wire.MediaPeerType.Android;
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.MediaMirrorRoutes.PairPath);
        var body = new Wire.MediaPairingRequest(origin, context.OperationId, command.PeerId, kind, selection.LocalRoot,
            selection.ProfileId, command.ExpectedRevision);
        return SendAsync<Wire.MediaPairingResponse, MediaSyncState>(new(HttpMethod.Post, route) { Content = JsonContent.Create(body) },
            origin, "media-pairing", "media-pairing-receipt-mismatch", response =>
                response.OperationId == body.OperationId && response.PeerId == body.PeerId &&
                response.State is { } state && state.Revision > body.ExpectedRevision && MapMedia(state) is { } mapped &&
                mapped.Peers.Any(peer => peer.PeerId == body.PeerId && peer.Kind == selection.Kind) &&
                mapped.Shares.Count(share => share.PeerId == body.PeerId) == 1 ? mapped : null, token);
    }

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> SetMirrorPausedAsync(SetMirrorPaused command, CommandContext context, CancellationToken token) =>
        SendMirrorControlAsync(command.ShareId, command.Paused ? Wire.MediaMirrorAction.Pause : Wire.MediaMirrorAction.Resume,
            command.ExpectedRevision, context, token);

    /// <inheritdoc />
    public Task<Outcome<MediaSyncState>> RescanMirrorAsync(RescanMediaShare command, CommandContext context, CancellationToken token) =>
        SendMirrorControlAsync(command.ShareId, Wire.MediaMirrorAction.Rescan, null, context, token);

    private Task<Outcome<MediaSyncState>> SendMirrorControlAsync(Guid shareId, Wire.MediaMirrorAction action, long? expected,
        CommandContext context, CancellationToken token)
    {
        if (context.Request.OriginInstanceId is not { } origin || origin == Guid.Empty || context.OperationId == Guid.Empty)
            return Task.FromResult(Invalid<MediaSyncState>("media-mirror-origin-required"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.MediaMirrorRoutes.ControlPath);
        var body = new Wire.MediaMirrorControlRequest(origin, context.OperationId, shareId, action, expected);
        return SendAsync<Wire.MediaMirrorControlResponse, MediaSyncState>(new(HttpMethod.Post, route) { Content = JsonContent.Create(body) },
            origin, "media-mirror-control", "media-mirror-receipt-mismatch", response =>
            {
                if (response.OperationId != context.OperationId || response.ShareId != shareId || response.State is not { } wire ||
                    MapMedia(wire) is not { } mapped || mapped.Shares.FirstOrDefault(share => share.ShareId == shareId) is not { } affected ||
                    !mapped.Peers.Any(peer => peer.PeerId == affected.PeerId && peer.Kind == MediaPeerKind.PcMirror))
                    return null;
                return action == Wire.MediaMirrorAction.Rescan
                    ? mapped
                    : affected.Paused == (action == Wire.MediaMirrorAction.Pause) && affected.Revision > expected ? mapped : null;
            }, token);
    }

    private static MediaSyncState? MapMedia(Wire.MediaSyncSnapshot state)
    {
        if (state.Revision < 0 || state.Peers.IsDefault || state.Shares.IsDefault ||
            !state.Peers.All(peer => peer is { PeerId: var id, Name.Length: > 0, ConnectionGeneration: >= 0 } && id != Guid.Empty &&
                Enum.IsDefined(peer.Kind) && (!peer.Connected || peer.ConnectionKnown)) ||
            state.Peers.Select(peer => peer.PeerId).Distinct().Count() != state.Peers.Length)
            return null;
        var peers = state.Peers.ToDictionary(peer => peer.PeerId);
        if (state.Shares.Any(share => share is null || share.ShareId == Guid.Empty || !peers.ContainsKey(share.PeerId) || share.Revision < 0 ||
                !Enum.IsDefined(share.Phase) || (share.TotalFiles is null) != (share.FilesReceived is null) ||
                share.FilesReceived < 0 || share.TotalFiles < 0 || share.FilesReceived > share.TotalFiles) ||
            state.Shares.Select(share => share.ShareId).Distinct().Count() != state.Shares.Length)
            return null;
        return new(state.Revision, [.. state.Peers.Select(peer => new MediaPeer(peer.PeerId, peer.Name,
                peer.Kind == Wire.MediaPeerType.PcMirror ? MediaPeerKind.PcMirror : MediaPeerKind.Android, peer.Connected, peer.ConnectionGeneration)
                { ConnectionKnown = peer.ConnectionKnown })],
            [.. state.Shares.Select(share => new MediaShare(share.ShareId, share.PeerId, share.Paused, share.Revision)
            {
                Progress = share.Phase == Wire.MediaSharePhase.Unknown ? null : new((MediaSharePhase)share.Phase, share.FilesReceived,
                    share.TotalFiles, share.FailureCode)
            })]);
    }
}
