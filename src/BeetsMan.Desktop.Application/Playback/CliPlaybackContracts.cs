using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Identifies a supported external invocation shape.</summary>
/// <remarks>2026-11-24: Materialize documented contracts for FR-CLI-001#ac-profiles and ac-switch.</remarks>
public enum CliInvocationMode { OneShot, Session }

/// <summary>Contains the application fields for an editable local CLI profile.</summary>
public sealed record CliProfileDraft(Guid ProfileId, long Revision, int SchemaVersion, CliInvocationMode Mode, string Executable, ImmutableArray<string> Arguments, PlaybackCapabilities Capabilities, TimeSpan Timeout);

/// <summary>Contains validated capability mappings or the reasons the profile is invalid.</summary>
public sealed record CliProfileValidation(Guid ProfileId, long DraftRevision, PlaybackCapabilities Capabilities, ImmutableArray<ValidationIssue> Issues);

/// <summary>Identifies the saved local profile version.</summary>
public sealed record CliProfileReceipt(Guid ProfileId, long Revision);

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
