using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies the complete authorized pairing scope resolved by the host.</summary>
/// <remarks>2026-11-24: Bind STB10 to exact registered native identities and approved paths for FR-FILESYNC-001#ac-mirror and ac-layouts.</remarks>
public sealed record RegisteredMediaPairing(Guid LibraryId, Guid PeerId, string Name, MediaPeerType Kind, string FolderId,
    Uri SenderEndpoint, string SenderDeviceId, string SenderRoot, Uri ReceiverEndpoint, string ReceiverDeviceId, string ReceiverRoot);

/// <summary>Reports verified sender membership and whether the receiving peer has accepted the same share.</summary>
public sealed record MediaShareOffer(bool ReceiverAccepted);

/// <summary>Offers a registered media folder without changing the receiver's acceptance or local settings.</summary>
public interface IMediaShareOffer
{
    /// <summary>Creates safely missing sender configuration and verifies the resulting exact folder.</summary>
    Task<HostOutcome<MediaShareOffer>> OfferAsync(RegisteredMediaPairing target, CancellationToken token);
}

/// <summary>Describes verified receiver configuration independently of file transfer progress.</summary>
/// <remarks>2026-11-24: Project STB10 receiver acceptance for FR-FILESYNC-001#ac-layouts.</remarks>
internal enum AndroidShareCondition { AwaitingAcceptance, Ready, Paused }

/// <summary>Checks the current configuration of an authorized Android receiver without changing it.</summary>
internal interface IAndroidShareStatus
{
    /// <summary>Reads whether the approved folder is waiting for acceptance, ready, or paused.</summary>
    Task<HostOutcome<AndroidShareCondition>> ReadShareStatusAsync(RegisteredAndroidExport target, CancellationToken token);
}
