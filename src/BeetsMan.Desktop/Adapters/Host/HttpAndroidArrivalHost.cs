using System.Globalization;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.MediaSync;
using Wire = BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Adapters.Host;

/// <summary>Reads and validates the host's versioned Android arrival snapshot.</summary>
/// <remarks>2026-11-24: Use the registered HTTPS origin for FR-FILESYNC-001#ac-arrival.</remarks>
public sealed partial class HttpDevicePlaylistExportHost
{
    /// <inheritdoc />
    public Task<Outcome<AndroidArrivalFrame>> ReadArrivalsAsync(Guid? epoch, long? after, RequestContext context, CancellationToken token)
    {
        if (context.OriginInstanceId is not { } origin || origin == Guid.Empty)
            return Task.FromResult(Invalid<AndroidArrivalFrame>("android-arrival-origin-required"));
        if (epoch.HasValue != after.HasValue || epoch == Guid.Empty || after is < 0)
            return Task.FromResult(Invalid<AndroidArrivalFrame>("android-arrival-cursor-invalid"));
        var route = RegisteredHostEndpoint.Resolve(host, libraryId, Wire.AndroidArrivalRoute.ReadPath);
        if (epoch is { } current && after is { } revision)
            route = new UriBuilder(route) { Query = $"epoch={current:D}&after={revision.ToString(CultureInfo.InvariantCulture)}" }.Uri;
        return SendAsync<Wire.AndroidArrivalSnapshot, AndroidArrivalFrame>(new(HttpMethod.Get, route), origin, "android-arrival",
            "android-arrival-response-mismatch", frame => ProjectArrival(frame, epoch, after), token);
    }

    private static AndroidArrivalFrame? ProjectArrival(Wire.AndroidArrivalSnapshot snapshot, Guid? epoch, long? after)
    {
        if (snapshot.Epoch == Guid.Empty || snapshot.Revision < 0 || snapshot.Peers.IsDefault ||
            !snapshot.Baseline && (snapshot.Epoch != epoch || snapshot.Revision < after) ||
            snapshot.Peers.Any(peer => peer is null || peer.PeerId == Guid.Empty || peer.ArrivedAtRevision is { } marked &&
                (snapshot.Baseline || !peer.Connected || marked <= 0 || marked > snapshot.Revision)) ||
            snapshot.Peers.Select(peer => peer.PeerId).Distinct().Count() != snapshot.Peers.Length)
            return null;
        return new(snapshot.Epoch, snapshot.Revision, snapshot.Baseline,
            [.. snapshot.Peers.Select(peer => new AndroidArrivalConnection(peer.PeerId, peer.Connected, peer.ArrivedAtRevision))]);
    }
}
