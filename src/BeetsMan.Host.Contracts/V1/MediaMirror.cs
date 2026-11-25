using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Identifies an authorized media peer without exposing native credentials or endpoints.</summary>
/// <remarks>2026-11-24: Publish the STB10 media directory for FR-FILESYNC-001#ac-mirror and ac-arrival.</remarks>
public enum MediaPeerType { PcMirror = 1, Android = 2 }

/// <summary>Reports the condition of a registered media share.</summary>
/// <remarks>2026-11-24: A verified Android folder can be ready before a transfer starts for FR-FILESYNC-001#ac-layouts.</remarks>
public enum MediaSharePhase { Unknown, AwaitingAcceptance, Paused, Transferring, Complete, Disconnected, Failed, Ready }

/// <summary>Returns the registered peer identity and latest confirmed connection state.</summary>
public sealed record MediaPeerSnapshot(Guid PeerId, string Name, MediaPeerType Kind, bool Connected, bool ConnectionKnown, long ConnectionGeneration);

/// <summary>Returns a share's control revision and last verified file progress.</summary>
public sealed record MediaShareSnapshot(Guid ShareId, Guid PeerId, bool Paused, long Revision, MediaSharePhase Phase,
    long? FilesReceived = null, long? TotalFiles = null, string? FailureCode = null);

/// <summary>Returns the current library-scoped media directory at one observation revision.</summary>
public sealed record MediaSyncSnapshot(long Revision, ImmutableArray<MediaPeerSnapshot> Peers, ImmutableArray<MediaShareSnapshot> Shares);

/// <summary>Enumerates the authenticated PC mirror commands.</summary>
public enum MediaMirrorAction { Pause = 1, Resume = 2, Rescan = 3 }

/// <summary>Submits one action on a registered mirror with its stable originating operation.</summary>
public sealed record MediaMirrorControlRequest(Guid OriginInstanceId, Guid OperationId, Guid ShareId, MediaMirrorAction Action,
    long? ExpectedRevision);

/// <summary>Returns the original result of the mirror control operation.</summary>
public sealed record MediaMirrorControlResponse(Guid OperationId, Guid ShareId, MediaSyncSnapshot State);

/// <summary>Approves a configured media destination at a specific library directory revision.</summary>
/// <remarks>2026-11-24: Carry the STB10 proposal and stable operation ID for FR-FILESYNC-001#ac-mirror and ac-layouts.</remarks>
public sealed record MediaPairingRequest(Guid OriginInstanceId, Guid OperationId, Guid PeerId, MediaPeerType Kind,
    string DestinationRoot, string? ProfileId, long ExpectedRevision);

/// <summary>Returns the original approved peer and its scoped share snapshot.</summary>
public sealed record MediaPairingResponse(Guid OperationId, Guid PeerId, MediaSyncSnapshot State);

/// <summary>Defines the authenticated V1 media directory and mirror control routes.</summary>
public static class MediaMirrorRoutes
{
    public const string ReadPattern = "/api/v1/libraries/{libraryId:guid}/media-sync";
    public const string ControlPattern = "/api/v1/libraries/{libraryId:guid}/media-sync/mirror-control";
    public const string PairPattern = "/api/v1/libraries/{libraryId:guid}/media-sync/pair";

    /// <summary>Returns the relative path for a library's current media directory.</summary>
    public static string ReadPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/media-sync";

    /// <summary>Returns the relative path for an authenticated mirror control operation.</summary>
    public static string ControlPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/media-sync/mirror-control";

    /// <summary>Returns the relative path for an authenticated pairing approval.</summary>
    public static string PairPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/media-sync/pair";
}
