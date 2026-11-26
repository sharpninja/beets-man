using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Identifies a supported external invocation shape.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-CLI-001#ac-profiles and ac-switch.</remarks>
public enum CliInvocationMode { OneShot, Session }

/// <summary>Identifies the app action a CLI profile can map to a process invocation or a session request.</summary>
/// <remarks>2026-11-25: Define the action vocabulary for FR-CLI-001#ac-profiles and TR-CLI-ARCH-001.</remarks>
public enum CliPlaybackAction { Play, Pause, Stop, Seek, Status }

/// <summary>Maps one action to separate argv tokens or a session request kind.</summary>
/// <remarks>2026-11-25: Keep paths and positions as values for FR-CLI-001#ac-profiles and ac-switch.</remarks>
public sealed record CliActionMapping(CliPlaybackAction Action, ImmutableArray<string> Arguments, string? RequestKind = null);

/// <summary>Names the whole-token values that can be bound to a one-shot action's argument vector.</summary>
/// <remarks>2026-11-25: Share validation and direct invocation for FR-CLI-001#ac-profiles and ac-switch.</remarks>
public static class CliArgumentToken
{
    public const string MediaPath = "{path}";
    public const string PositionMilliseconds = "{positionMs}";
}

/// <summary>Maps the worker's top-level JSON reply fields and status values to the playback contract.</summary>
/// <remarks>2026-11-25: Declare reply and status mappings for FR-CLI-001#ac-profiles.</remarks>
public sealed record CliReplyMapping(string StatusField, ImmutableDictionary<string, TransportStatus> StatusValues,
    string? PositionMillisecondsField = null, string? DurationMillisecondsField = null);

/// <summary>Contains the application fields for an editable local CLI profile.</summary>
/// <remarks>2026-11-25: Add versioned action and reply mappings for FR-CLI-001#ac-profiles.</remarks>
public sealed record CliProfileDraft(Guid ProfileId, long Revision, int SchemaVersion, CliInvocationMode Mode, string Executable,
    ImmutableArray<string> Arguments, PlaybackCapabilities Capabilities, TimeSpan Timeout,
    ImmutableArray<CliActionMapping> Actions = default, CliReplyMapping? Reply = null);

/// <summary>Contains validated capability mappings or the reasons the profile is invalid.</summary>
public sealed record CliProfileValidation(Guid ProfileId, long DraftRevision, PlaybackCapabilities Capabilities, ImmutableArray<ValidationIssue> Issues);

/// <summary>Identifies the saved local profile version.</summary>
public sealed record CliProfileReceipt(Guid ProfileId, long Revision);

/// <summary>Defines stable identifiers for the two desktop playback strategies.</summary>
/// <remarks>2026-11-25: Use the same identities in switch proposals and playback state for FR-CLI-001#ac-switch.</remarks>
public static class PlaybackStrategyIds
{
    public const string SoundFlow = "soundflow";
    public const string Cli = "cli";
}

/// <summary>A disposable external playback transport owned by one active workspace.</summary>
/// <remarks>2026-11-25: Close the selected worker during strategy handoff for FR-CLI-001#ac-switch.</remarks>
public interface ICliPlaybackTarget : IPlaybackTransportTarget, IDisposable;

/// <summary>Supplies the local profile reader and a factory that constructs a stopped strategy.</summary>
/// <remarks>2026-11-25: Compose a validated worker without invoking Play for FR-CLI-001#ac-switch.</remarks>
public sealed record CliPlaybackRegistration(ICliProfileStore Profiles, Func<CliProfileDraft, ICliPlaybackTarget> Create);

/// <summary>Contains a pending switch scoped to an active playback revision.</summary>
public sealed record PlaybackStrategyProposal(Guid SessionId, Guid ProposalId, long Revision, string StrategyId, PlaybackCapabilities Capabilities, bool RequiresConfirmation, Guid? ProfileId);

/// <summary>Reads the proposed consequences of switching strategies.</summary>
public sealed record GetPlaybackStrategyProposal(Guid SessionId, string StrategyId, Guid? ProfileId = null) : IQuery<PlaybackStrategyProposal>;

/// <summary>Loads an existing local profile or an initial profile draft.</summary>
public sealed record GetCliProfile(Guid? ProfileId = null) : IQuery<CliProfileDraft>;

/// <summary>Validates a captured profile draft.</summary>
public sealed record ValidateCliProfile(CliProfileDraft ProfileDraft) : IQuery<CliProfileValidation>;

/// <summary>Stores the current profile at its expected revision.</summary>
public sealed record SaveCliProfile(CliProfileDraft ProfileDraft, long ExpectedRevision) : ICommand<CliProfileReceipt>;
