using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a library entity whose bytes are exported.</summary>
/// <remarks>2026-11-24: Keep media and playlist ownership distinct for FR-FILESYNC-001#ac-safety.</remarks>
public readonly record struct ExportContentSource(ExportContentKind Kind, Guid EntityId);

/// <summary>Identifies the source collection for an exported file.</summary>
public enum ExportContentKind { Audio = 1, Playlist = 2 }

/// <summary>Records a share-relative file owned by this device export and the bytes that were published.</summary>
public sealed record OwnedDeviceExportFile(ExportContentSource Source, string Path, string ContentIdentity);

/// <summary>Contains the last published ownership manifest for one registered Android share.</summary>
public sealed record PublishedDeviceExportManifest(Guid LibraryId, Guid DeviceId, string ShareId, long Revision,
    ImmutableArray<OwnedDeviceExportFile> Files);

/// <summary>Describes one desired media or playlist path before changing the active share.</summary>
/// <param name="Source">The stable library identity.</param>
/// <param name="Path">The device-relative path.</param>
/// <param name="WriteRequired">Whether the destination needs published content. False for audio already verified on the receiver.</param>
/// <param name="SourceReadable">Whether the source was successfully bound to readable staged bytes.</param>
/// <param name="ContentIdentity">The SHA-256 digest of the staged bytes when a write is required.</param>
public sealed record DeviceExportFileCandidate(ExportContentSource Source, string Path, bool WriteRequired, bool SourceReadable, string? ContentIdentity);

/// <summary>Distinguishes regular media from path entries that cannot be overwritten.</summary>
public enum DeviceExportPathKind { File = 1, Directory = 2, Link = 3 }

/// <summary>Describes the active share entry observed at one exact relative path.</summary>
public sealed record ObservedDeviceExportPath(string Path, DeviceExportPathKind Kind, string? ContentIdentity = null, long? Length = null);

/// <summary>Captures the active share inventory relevant to the prior and proposed paths and their ancestors.</summary>
public sealed record DeviceExportShareSnapshot(Guid LibraryId, Guid DeviceId, string ShareId, ImmutableArray<ObservedDeviceExportPath> Paths);

/// <summary>Identifies the file action shown in a publication preview.</summary>
public enum DeviceExportOperationKind { Add = 1, Replace = 2, Move = 3, Remove = 4 }

/// <summary>Distinguishes an ordinary staged-list publication from explicit layout reconciliation.</summary>
/// <remarks>2026-11-24: Preserve unrelated owned files during playlist drops for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public enum DeviceExportPublicationMode { AddToShare = 1, ReconcileLayout = 2 }

/// <summary>Binds a preview action to expected bytes at its old and new paths.</summary>
public sealed record DeviceExportFileOperation(DeviceExportOperationKind Kind, OwnedDeviceExportFile? Before, OwnedDeviceExportFile? After);

/// <summary>Identifies a path issue that blocks device export.</summary>
public sealed record DeviceExportPreviewIssue(string Code, string Path);

/// <summary>Identifies the exact prepared plan and path set requiring explicit removal approval.</summary>
public sealed record DeviceExportRemovalProposal(Guid ProposalId, Guid PlanId, long PlanRevision, ImmutableArray<string> Paths);

/// <summary>Records the caller's approval of a particular removal proposal.</summary>
public sealed record DeviceExportRemovalApproval(Guid ProposalId, Guid PlanId, long PlanRevision);

/// <summary>Contains an approved staged entry and any generated playlist payload.</summary>
/// <remarks>2026-11-24: Retain reviewed layout paths and rendered playlist content through publication for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record ReviewedDeviceExportEntry(ExportEntry Entry, string? PlaylistDigest);

/// <summary>Identifies the source layout that must remain assigned until a replacement publishes.</summary>
/// <remarks>2026-11-24: Bind STB20 profile changes to the original staged scope for FR-FILESYNC-001#ac-layouts and ac-safety.</remarks>
public sealed record DeviceExportSourceProfile(string Id, long Revision);

/// <summary>Captures the preview and the ownership projection a later successful publication would commit.</summary>
public sealed record PreparedDeviceExportPublication(DeviceExportPlan Plan, RegisteredAndroidExport Destination, long ManifestRevision,
    ImmutableArray<DeviceExportFileOperation> Operations, ImmutableArray<DeviceExportPreviewIssue> Issues,
    ImmutableArray<OwnedDeviceExportFile> NextOwnedFiles, DeviceExportRemovalProposal? RemovalProposal)
{
    /// <summary>Gets the staged entries to commit with a successfully published layout correction.</summary>
    public ImmutableArray<ReviewedDeviceExportEntry> LayoutEntries { get; init; } = [];

    /// <summary>Gets the original layout if publication will change the plan's profile.</summary>
    public DeviceExportSourceProfile? PreviousProfile { get; init; }
}
