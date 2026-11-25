namespace BeetsMan.Host.Application;

/// <summary>Binds an authorized desktop mirror to both ends of its exact native folder.</summary>
/// <remarks>2026-11-24: Scope FR-FILESYNC-001#ac-mirror to the registered desktop and full library root.</remarks>
public sealed record RegisteredPcMirror(Guid LibraryId, Guid PeerId, string FolderId, Uri ReceiverEndpoint, string ReceiverDeviceId,
    string ReceiverRoot, Uri SenderEndpoint, string SenderDeviceId, string MusicRoot);

/// <summary>Describes the condition of the receiving mirror.</summary>
public enum PcMirrorPhase { AwaitingAcceptance, Paused, Transferring, Complete, Disconnected, Failed }

/// <summary>Reports confirmed connection, pause state and file progress from the receiver.</summary>
public sealed record PcMirrorNativeState(bool Connected, bool Paused, PcMirrorPhase Phase, long? FilesReceived, long? TotalFiles,
    string? FailureCode = null);

/// <summary>Identifies the only permitted native operations for a PC mirror.</summary>
public enum PcMirrorAction { Observe, Pause, Resume, Rescan }

/// <summary>Reads and changes only the authorized full-library mirror.</summary>
public interface IPcMirrorControl
{
    /// <summary>Validates both ends, runs the specified operation, and returns the known native state.</summary>
    Task<HostOutcome<PcMirrorNativeState>> ApplyAsync(RegisteredPcMirror target, PcMirrorAction action,
        PcMirrorNativeState? previous, CancellationToken token);
}
