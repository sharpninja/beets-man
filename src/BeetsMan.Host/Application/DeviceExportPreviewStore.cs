using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a request to prepare a particular staged plan revision.</summary>
/// <remarks>2026-11-24: Bind the review to the exact plan for FR-FILESYNC-001#ac-safety.</remarks>
public sealed record DeviceExportPreviewIntent(Guid OperationId, Guid PlanId, long ExpectedPlanRevision, Guid DeviceId,
    string ProfileId, long ProfileRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImmutableDictionary<Guid, string>? RelativePathsByTrack = null);

/// <summary>Contains the host data needed to prepare an additive publication.</summary>
public sealed record DeviceExportPreviewSources(DeviceExportPlan Plan, RegisteredAndroidExport Destination,
    PublishedDeviceExportManifest Manifest, ImmutableDictionary<Guid, string> PlaylistDigests);

/// <summary>Represents either the original review receipt or the inputs to a new review.</summary>
public abstract record DeviceExportPreviewPreparation
{
    private DeviceExportPreviewPreparation() { }

    /// <summary>The same operation has already saved this private descriptor.</summary>
    public sealed record Replay(string Digest) : DeviceExportPreviewPreparation;

    /// <summary>The plan and share ownership from which to construct a new review.</summary>
    public sealed record New(DeviceExportPreviewSources Sources) : DeviceExportPreviewPreparation;
}

/// <summary>Reads and records durable previews for an authenticated library.</summary>
/// <remarks>2026-11-24: Persist the plan revision and exact preview together for FR-FILESYNC-001#ac-safety.</remarks>
public interface IDeviceExportPreviewStore
{
    /// <summary>Reads the original receipt or the authorized plan at the expected revision.</summary>
    Task<HostOutcome<DeviceExportPreviewPreparation>> ReadPreviewSourcesAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPreviewIntent intent, CancellationToken token);

    /// <summary>Saves the descriptor and advances the plan revision if its source inputs remain current.</summary>
    Task<HostOutcome<string>> SavePreviewAsync(Guid libraryId, AuthenticatedHostInstance caller, DeviceExportPreviewIntent intent,
        DeviceExportPreviewSources sources, string digest, CancellationToken token);

    /// <summary>Reads the review descriptor associated with the approved plan revision.</summary>
    Task<HostOutcome<string>> ReadPreviewDigestAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid planId, long revision,
        CancellationToken token);
}

/// <summary>Stores immutable bytes outside the share, indexed by content identity.</summary>
public interface IDeviceExportPayloadStore : IDeviceExportContentReader, IStagedPlaylistContent
{
    /// <summary>Copies a payload from its current position and returns its SHA-256 digest.</summary>
    Task<string> StoreAsync(Stream content, CancellationToken token);

    /// <summary>Opens a stored payload for the caller to verify and dispose.</summary>
    ValueTask<Stream?> OpenDigestAsync(string digest, CancellationToken token);
}
