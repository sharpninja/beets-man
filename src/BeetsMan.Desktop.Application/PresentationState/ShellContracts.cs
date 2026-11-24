using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.PresentationState;

/// <summary>Selects the desktop workspace.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-PLAYER-001#ac-modes, FR-FILESYNC-001#ac-arrival, and FR-DISPATCH-001#ac-update.</remarks>
public enum Workspace { Bootstrap, Library, NowPlaying, Sync, Settings, Diagnostics, Extensions, HostLibrary, Records }

/// <summary>Identifies a player presentation.</summary>
public enum PresentationMode { Normal, Minimum, BoundedVisualization, FullVisualization }

/// <summary>Distinguishes the reason an overlay was closed.</summary>
public enum OverlayCloseReason { Cancel, Close, Dismiss }

/// <summary>Selects a workspace and optionally its requested peer.</summary>
public sealed record WorkspaceRoute(Workspace Workspace, Guid? PeerId = null);

/// <summary>Selects a mode or restores the prior windowed presentation.</summary>
public sealed record ModeSelection(PresentationMode? Mode) { public static ModeSelection RestoreWindowed { get; } = new((PresentationMode?)null); }

/// <summary>Describes an overlay proposal and the application owner supplying its input.</summary>
public abstract record OverlayIntent
{
    public sealed record ChoosePresentation : OverlayIntent;
    public sealed record PairMediaPeer : OverlayIntent;
    public sealed record RegisterInstance(Guid LibraryId) : OverlayIntent;
    public sealed record RevokeInstance(Guid InstanceId, long RegistryRevision) : OverlayIntent;
}

/// <summary>Identifies a currently active window-owned overlay.</summary>
public sealed record OverlayState(Guid OverlayId, Guid OwnerId, OverlayIntent Intent, string? FocusTarget = null);

/// <summary>Contains the current shell navigation, playback identity and overlay stack.</summary>
public sealed record ShellState(Guid WindowId, long Revision, WorkspaceRoute Route, PresentationMode Mode, PresentationMode PriorWindowedMode, Guid PlaybackSessionId, ImmutableArray<OverlayState> Overlays, string? FocusTarget = null);

/// <summary>Changes the active workspace.</summary>
public sealed record NavigateWorkspace(Guid WindowId, WorkspaceRoute Route) : ICommand<ShellState>;

/// <summary>Opens the requested overlay for its owner.</summary>
public sealed record OpenOverlay(Guid WindowId, Guid OwnerId, OverlayIntent Intent) : ICommand<ShellState>;

/// <summary>Applies or restores the requested player presentation.</summary>
public sealed record ChangePresentation(Guid WindowId, ModeSelection ModeOrRestore) : ICommand<ShellState>;

/// <summary>Closes the identified overlay and restores focus.</summary>
public sealed record CloseOverlay(Guid WindowId, Guid OverlayId, OverlayCloseReason Reason) : ICommand<ShellState>;

/// <summary>Applies one live registered Android connection transition.</summary>
public sealed record ApplyAndroidPeerArrival(Guid WindowId, Guid PeerId, long ConnectionGeneration) : ICommand<ShellState>;
