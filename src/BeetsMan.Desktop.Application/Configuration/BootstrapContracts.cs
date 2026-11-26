using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Carries local configuration paths and configured log filters.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-CONFIG-001#ac-defaults, ac-overrides, ac-repair, and ac-logs.</remarks>
public sealed record BootstrapSettings(string SettingsPath, string DatabasePath, string MusicRoot, ImmutableDictionary<string, string> LogLevels,
    RollingLogSettings? RollingLog = null);

/// <summary>Contains the local rolling-file logging settings.</summary>
public sealed record RollingLogSettings(bool Enabled, string Directory, int RetainedFileCount);

/// <summary>Binds database creation approval to the validated path.</summary>
public sealed record DatabaseCreationApproval(Guid ProposalId, string DatabasePath);

/// <summary>Identifies bootstrap readiness and restart state.</summary>
public enum BootstrapStatus { FirstRun, RepairRequired, CreationApprovalRequired, Ready, RestartRequired }

/// <summary>Contains effective and proposed bootstrap settings at their revision.</summary>
public sealed record BootstrapState(long Revision, BootstrapStatus Status, BootstrapSettings? Effective, BootstrapSettings? Proposed, DatabaseCreationApproval? CreationProposal = null, ImmutableArray<ValidationIssue> Issues = default);

/// <summary>Reads bootstrap state without opening the normal workspace.</summary>
public sealed record GetBootstrapState : IQuery<BootstrapState>;

/// <summary>Re-evaluates repaired desktop configuration.</summary>
public sealed record RetryDesktopBootstrap : ICommand<BootstrapState>;

/// <summary>Initializes validated first-run settings.</summary>
public sealed record InitializeDesktop(BootstrapSettings Settings, DatabaseCreationApproval? CreationApproval = null) : ICommand<BootstrapState>;

/// <summary>Saves local settings at the expected revision.</summary>
public sealed record SaveBootstrapSettings(BootstrapSettings Settings, long ExpectedRevision, DatabaseCreationApproval? CreationApproval = null) : ICommand<BootstrapState>;

/// <summary>Specifies a structured diagnostics filter.</summary>
public sealed record DiagnosticsFilter(string? Category = null, Guid? CorrelationId = null, string? PluginId = null);

/// <summary>Contains a redacted structured diagnostics entry.</summary>
/// <remarks>2026-11-25: Identify the exact plugin version for FR-PLUGINS-001#ac-recovery.</remarks>
public sealed record DiagnosticEntry(DateTimeOffset OccurredAt, string Level, string Category, string Code, Guid? CorrelationId, string? PluginId,
    string? PluginVersion = null);

/// <summary>Returns a page of redacted diagnostics.</summary>
public sealed record DiagnosticsPage(DiagnosticsFilter Filter, ImmutableArray<DiagnosticEntry> Entries, string? NextPage);

/// <summary>Identifies completion of the platform log-folder handoff.</summary>
public sealed record FolderOpenReceipt(string FolderPath);

/// <summary>Reads the requested page of diagnostics.</summary>
public sealed record GetDiagnostics(DiagnosticsFilter Filter, string? Page = null) : IQuery<DiagnosticsPage>;

/// <summary>Asks the platform to open the configured log folder.</summary>
public sealed record OpenLogFolder : ICommand<FolderOpenReceipt>;
