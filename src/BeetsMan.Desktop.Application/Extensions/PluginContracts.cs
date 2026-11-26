using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Distinguishes local plugin trust policies.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-PLUGINS-001#ac-manage, ac-visualize, and ac-recovery.</remarks>
public enum PluginTrust { Untrusted, Local }

/// <summary>Identifies a worker lifecycle state.</summary>
public enum PluginWorkerStatus { Stopped, Starting, Ready, Failed }

/// <summary>Classifies an approved contribution.</summary>
public enum PluginContributionKind { Action, Panel, Visualization }

/// <summary>Describes a validated contribution owned by one installed worker.</summary>
/// <remarks>2026-11-25: Pin optional host action grants to the admitted manifest for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record PluginContribution(string ContributionId, PluginContributionKind Kind, string? HostAction = null, PluginPanelDefinition? Panel = null);

/// <summary>Describes one installed version and its negotiated contributions.</summary>
/// <remarks>2026-11-25: Make package compatibility visible before trust or activation for FR-PLUGINS-001#ac-manage.</remarks>
public sealed record PluginInfo(string PluginId, string Version, PluginTrust Trust, bool Enabled, PluginWorkerStatus Worker,
    ImmutableArray<PluginContribution> Contributions, string? FailureCode = null, bool Compatible = true, Guid? ActivationId = null,
    ImmutableArray<string> Visualizations = default);

/// <summary>Returns the current revision of the local plugin registry.</summary>
/// <remarks>2026-11-25: Include runtime revisions and recovery suppression for FR-PLUGINS-001#ac-recovery.</remarks>
public sealed record PluginState(long Revision, ImmutableArray<PluginInfo> Plugins, bool RecoveryMode = false);

/// <summary>Reads local registry or plugin version status.</summary>
public sealed record GetPluginState(string? PluginId = null, string? Version = null) : IQuery<PluginState>;

/// <summary>Installs or updates from an acquired local package selection.</summary>
public sealed record InstallPluginPackage(Guid PackageSelectionToken) : ICommand<PluginState>;

/// <summary>Applies enablement to an identified plugin version.</summary>
public sealed record SetPluginEnabled(string PluginId, string Version, bool Enabled) : ICommand<PluginState>;

/// <summary>Records local trust for the exact plugin version.</summary>
public sealed record SetPluginTrust(string PluginId, string Version, PluginTrust Trust) : ICommand<PluginState>;

/// <summary>Removes one installed version and its active contributions.</summary>
public sealed record RemovePlugin(string PluginId, string Version) : ICommand<PluginState>;

/// <summary>Restarts the identified trusted worker.</summary>
public sealed record RestartPluginWorker(string PluginId, string Version) : ICommand<PluginState>;
