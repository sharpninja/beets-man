using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.MediaSync;

/// <summary>Distinguishes a PC mirror from a device-specific export.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-FILESYNC-001#ac-mirror, ac-layouts, ac-safety, ac-arrival, and ac-playlist-drop.</remarks>
public enum MediaPeerKind { PcMirror, Android }

/// <summary>Contains visible peer and share status.</summary>
public sealed record MediaPeer(Guid PeerId, string Name, MediaPeerKind Kind, bool Connected, long ConnectionGeneration);

/// <summary>Contains the settings of one library-owned share.</summary>
public sealed record MediaShare(Guid ShareId, Guid PeerId, bool Paused, long Revision);

/// <summary>Describes a selected share in a pairing proposal.</summary>
public sealed record ShareSelection(MediaPeerKind Kind, string LocalRoot, string? ProfileId);

/// <summary>Returns versioned media sync state.</summary>
public sealed record MediaSyncState(long Revision, ImmutableArray<MediaPeer> Peers, ImmutableArray<MediaShare> Shares);

/// <summary>Identifies a transfer checkpoint phase.</summary>
public enum TransferPhase { Queued, Running, Complete, Paused, Failed }

/// <summary>Returns progress or an actionable transfer failure.</summary>
public sealed record MediaTransferState(Guid TransferId, Guid ShareId, long Revision, TransferPhase Phase, long BytesTransferred, long? TotalBytes, string? FailureCode = null, bool Retryable = false);

/// <summary>Reads registered media peers and their shares.</summary>
public sealed record GetMediaSyncState : IQuery<MediaSyncState>;

/// <summary>Accepts the current peer and approved share proposal.</summary>
public sealed record PairMediaPeer(Guid PeerId, ShareSelection ShareSelection, long ExpectedRevision) : ICommand<MediaSyncState>;

/// <summary>Requests a scan of the specified share.</summary>
public sealed record RescanMediaShare(Guid ShareId) : ICommand<MediaSyncState>;

/// <summary>Changes the pause state of a PC mirror.</summary>
public sealed record SetMirrorPaused(Guid ShareId, bool Paused, long ExpectedRevision) : ICommand<MediaSyncState>;

/// <summary>Reads the checkpoint and status of the specified transfer.</summary>
public sealed record GetMediaTransfer(Guid TransferId) : IQuery<MediaTransferState>;

/// <summary>Resumes and reconciles the recorded transfer.</summary>
public sealed record ResumeMediaTransfer(Guid TransferId, long ExpectedRevision) : ICommand<MediaTransferState>;

/// <summary>Identifiers the availability of a resolved audio destination.</summary>
public enum ExportAudioStatus { Present, Staged, Missing, Unavailable }

/// <summary>Identifies one resolved and versioned player profile.</summary>
public sealed record DeviceProfile(string ProfileId, long Revision, string Name, string RelativePathTemplate, string? PlaylistFormat);

/// <summary>Contains eligible layouts for the identified device.</summary>
public sealed record DeviceLayoutsSnapshot(Guid DeviceId, ImmutableArray<DeviceProfile> Profiles);

/// <summary>Identifies a staged export item by source and intended destination.</summary>
public abstract record ExportEntry(Guid EntryId, string Destination)
{
    public sealed record Audio(Guid EntryId, string Destination, Guid TrackId, ExportAudioStatus Status) : ExportEntry(EntryId, Destination);
    public sealed record Playlist(Guid EntryId, string Destination, Guid PlaylistId, string Format,
        ImmutableArray<ExportTrackReference> Tracks) : ExportEntry(EntryId, Destination);
}

/// <summary>Contains an ordered playlist track reference and its intended device path.</summary>
public sealed record ExportTrackReference(Guid TrackId, string Destination, ExportAudioStatus Status);

/// <summary>Contains the staged list for one device and player profile.</summary>
public sealed record DeviceExportPlan(Guid? PlanId, long? PlanRevision, Guid DeviceId, string ProfileId, long ProfileRevision, ImmutableArray<ExportEntry> Entries);

/// <summary>Captures operator edits addressing path validation.</summary>
public sealed record LayoutEdits(ImmutableDictionary<Guid, string> RelativePathsByTrack);

/// <summary>Binds removal approval to a specific proposed path set and plan revision.</summary>
public sealed record OwnedRemovalProposal(Guid ProposalId, Guid PlanId, long PlanRevision, ImmutableArray<string> Paths);

/// <summary>Indicates that the operator approved a specific owned-removal proposal.</summary>
public sealed record OwnedRemovalApproval(Guid ProposalId, Guid PlanId, long PlanRevision);

/// <summary>Returns validation and the planned file changes before publication.</summary>
public sealed record DeviceExportPreview(DeviceExportPlan Plan, ImmutableArray<ValidationIssue> BlockingIssues, ImmutableArray<string> ProposedRemovals);

/// <summary>Identifies committed publication and its optional transfer.</summary>
public sealed record ExportPublicationReceipt(Guid PublicationId, Guid PlanId, long PlanRevision, Guid? TransferId);

/// <summary>Determines whether publication succeeded or needs the exact removal approval.</summary>
public abstract record DeviceExportSubmission
{
    public sealed record Published(ExportPublicationReceipt Receipt) : DeviceExportSubmission;
    public sealed record ConfirmationRequired(OwnedRemovalProposal Proposal) : DeviceExportSubmission;
}

/// <summary>Describes a staged list intent.</summary>
public abstract record DeviceExportEdit { public sealed record Remove(ImmutableArray<Guid> EntryIds) : DeviceExportEdit; }

/// <summary>Reads the active or explicitly selected list for the device.</summary>
public sealed record GetDeviceExportPlan(Guid DeviceId, Guid? PlanId = null) : IQuery<DeviceExportPlan>;

/// <summary>Stages ordered tracks for the identified scope.</summary>
public sealed record StageTracksForDevice(ImmutableArray<Guid> OrderedTrackIds, Guid DeviceId, string ProfileId, long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision) : ICommand<DeviceExportPlan>;

/// <summary>Stages a full ordered playlist and the audio missing from its device destinations.</summary>
public sealed record StagePlaylistForDevice(Guid PlaylistId, Guid DeviceId, string ProfileId, long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision) : ICommand<DeviceExportPlan>;

/// <summary>Applies a scoped edit to a staged export list.</summary>
public sealed record EditDeviceExportPlan(Guid PlanId, DeviceExportEdit Edit, long ExpectedPlanRevision) : ICommand<DeviceExportPlan>;

/// <summary>Reads the layouts supported by a device.</summary>
public sealed record GetDeviceLayouts(Guid DeviceId) : IQuery<DeviceLayoutsSnapshot>;

/// <summary>Prepares or recalculates a staged export preview without publishing it.</summary>
public sealed record PrepareDeviceExportPreview(Guid DeviceId, string ProfileId, long ProfileRevision, Guid? PlanId, long? ExpectedPlanRevision, LayoutEdits? LayoutEdits = null) : ICommand<DeviceExportPreview>;

/// <summary>Publishes the prepared revision with its optional scoped approval.</summary>
public sealed record PublishDeviceExport(Guid PlanId, long ExpectedPlanRevision, OwnedRemovalApproval? RemovalApproval = null) : ICommand<DeviceExportSubmission>;
