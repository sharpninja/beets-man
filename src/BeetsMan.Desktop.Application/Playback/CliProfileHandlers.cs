using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Reads and writes revisioned CLI profile definitions owned by this desktop.</summary>
/// <remarks>2026-11-25: Keep CLI executable configuration local for FR-CLI-001#ac-profiles and TR-CLI-ARCH-001.</remarks>
public interface ICliProfileStore
{
    /// <summary>Reads a saved profile, or null when the identity is absent.</summary>
    Task<Outcome<CliProfileDraft?>> ReadAsync(Guid profileId, CancellationToken token);

    /// <summary>Atomically saves a validated draft if its previous revision matches.</summary>
    Task<Outcome<CliProfileReceipt>> SaveAsync(CliProfileDraft profile, long expectedRevision, CancellationToken token);
}

/// <summary>Handles profile editing, structural validation, and revisioned local saves without launching a worker.</summary>
/// <remarks>2026-11-25: Bind FR-CLI-001#ac-profiles, TR-CLI-ARCH-001, and TEST-CLI-001 to typed application requests.</remarks>
public sealed class CliProfileHandlers(ICliProfileStore store) : IQueryHandler<GetCliProfile, CliProfileDraft>,
    IQueryHandler<ValidateCliProfile, CliProfileValidation>, ICommandHandler<SaveCliProfile, CliProfileReceipt>
{
    internal const int SupportedSchema = 1;
    private static readonly CliPlaybackAction[] TransportActions = [CliPlaybackAction.Play, CliPlaybackAction.Pause, CliPlaybackAction.Stop];
    private static readonly TransportStatus[] TransportStates = [TransportStatus.Playing, TransportStatus.Paused, TransportStatus.Stopped];

    /// <summary>Registers the profile query, validation query, and save command.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder.RegisterQuery<GetCliProfile, CliProfileDraft>(this)
        .RegisterQuery<ValidateCliProfile, CliProfileValidation>(this).RegisterCommand<SaveCliProfile, CliProfileReceipt>(this);

    /// <inheritdoc />
    public async Task<Outcome<CliProfileDraft>> HandleAsync(GetCliProfile request, RequestContext context, CancellationToken token)
    {
        if (request.ProfileId is null)
            return new Outcome<CliProfileDraft>.Success(new(Guid.NewGuid(), 0, SupportedSchema, CliInvocationMode.OneShot, "", [],
                PlaybackCapabilities.Transport, TimeSpan.FromSeconds(2), []));
        if (request.ProfileId == Guid.Empty)
            return new Outcome<CliProfileDraft>.Invalid([new("invalid-cli-profile-id")]);

        return await store.ReadAsync(request.ProfileId.Value, token).ConfigureAwait(false) switch
        {
            Outcome<CliProfileDraft?>.Success { Value: { } profile } => new Outcome<CliProfileDraft>.Success(profile),
            Outcome<CliProfileDraft?>.Success => new Outcome<CliProfileDraft>.Invalid([new("cli-profile-not-found")]),
            Outcome<CliProfileDraft?>.Invalid rejected => new Outcome<CliProfileDraft>.Invalid(rejected.Issues),
            Outcome<CliProfileDraft?>.Conflict conflict => new Outcome<CliProfileDraft>.Conflict(conflict.Code, conflict.ActualRevision),
            Outcome<CliProfileDraft?>.DependencyFailure unavailable => new Outcome<CliProfileDraft>.DependencyFailure(unavailable.Code, unavailable.Retryable),
            _ => new Outcome<CliProfileDraft>.Cancelled()
        };
    }

    /// <inheritdoc />
    public Task<Outcome<CliProfileValidation>> HandleAsync(ValidateCliProfile request, RequestContext context, CancellationToken token) =>
        Task.FromResult<Outcome<CliProfileValidation>>(token.IsCancellationRequested ? new Outcome<CliProfileValidation>.Cancelled() :
            new Outcome<CliProfileValidation>.Success(Validate(request.ProfileDraft)));

    /// <inheritdoc />
    public Task<Outcome<CliProfileReceipt>> HandleAsync(SaveCliProfile request, CommandContext context, CancellationToken token)
    {
        var profile = request.ProfileDraft;
        if (profile.Revision != request.ExpectedRevision)
            return Task.FromResult<Outcome<CliProfileReceipt>>(new Outcome<CliProfileReceipt>.Conflict("stale-cli-profile", profile.Revision));
        var report = Validate(profile);
        return report.Issues.IsEmpty ? store.SaveAsync(profile, request.ExpectedRevision, token) :
            Task.FromResult<Outcome<CliProfileReceipt>>(new Outcome<CliProfileReceipt>.Invalid(report.Issues));
    }

    /// <summary>Validates the schema and derives controls supported by both the profile and its actual mappings.</summary>
    public static CliProfileValidation Validate(CliProfileDraft profile)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        if (profile.ProfileId == Guid.Empty || profile.Revision is < 0 or long.MaxValue)
            issues.Add(new("invalid-cli-profile-id"));
        if (profile.SchemaVersion != SupportedSchema)
            issues.Add(new("unsupported-cli-schema"));
        if (!Enum.IsDefined(profile.Mode))
            issues.Add(new("invalid-cli-invocation-mode"));
        if (!NonBlankText(profile.Executable, 4096) || !IsArguments(profile.Arguments) || profile.Arguments.Any(IsPlaceholder))
            issues.Add(new("invalid-cli-arguments"));
        if (profile.Timeout < TimeSpan.FromMilliseconds(1) || profile.Timeout > TimeSpan.FromMinutes(5) ||
            profile.Timeout.Ticks % TimeSpan.TicksPerMillisecond != 0)
            issues.Add(new("invalid-cli-timeout"));

        var actions = new HashSet<CliPlaybackAction>();
        foreach (var entry in profile.Actions.IsDefault ? [] : profile.Actions)
        {
            if (entry is null || !ValidAction(profile.Mode, entry) || !actions.Add(entry.Action))
            {
                issues.Add(new("invalid-cli-action-map"));
                break;
            }
        }
        var transportMapped = TransportActions.All(actions.Contains);
        if (profile.Capabilities.HasFlag(PlaybackCapabilities.Transport) && !transportMapped)
            issues.Add(new("cli-transport-mappings-required"));
        var seekMapped = actions.Contains(CliPlaybackAction.Seek) && profile.Reply?.PositionMillisecondsField is not null;
        if (profile.Capabilities.HasFlag(PlaybackCapabilities.Seek) && !seekMapped)
            issues.Add(new("cli-seek-mapping-required"));

        if (!ValidReply(profile.Reply, transportMapped))
            issues.Add(new("invalid-cli-status-map"));

        var supported = PlaybackCapabilities.None;
        if (issues.Count == 0)
        {
            if (transportMapped)
                supported |= PlaybackCapabilities.Transport;
            if (seekMapped)
                supported |= PlaybackCapabilities.Seek;
        }
        return new(profile.ProfileId, profile.Revision, profile.Capabilities & supported, issues.ToImmutable());
    }

    private static bool ValidAction(CliInvocationMode mode, CliActionMapping entry)
    {
        if (!Enum.IsDefined(entry.Action) || !IsArguments(entry.Arguments))
            return false;
        if (mode == CliInvocationMode.Session)
            return entry.Arguments.IsEmpty && NonBlankText(entry.RequestKind, 128);
        return entry.RequestKind is null &&
            (entry.Action != CliPlaybackAction.Play || entry.Arguments.Contains(CliArgumentToken.MediaPath)) &&
            (entry.Action != CliPlaybackAction.Seek || entry.Arguments.Contains(CliArgumentToken.PositionMilliseconds));
    }

    private static bool ValidReply(CliReplyMapping? reply, bool transportMapped)
    {
        if (reply is null || reply.StatusValues is not { Count: > 0 and <= 32 } values ||
            values.Any(item => !NonBlankText(item.Key, 128) || !Enum.IsDefined(item.Value)))
            return false;
        string?[] fields = [reply.StatusField, reply.PositionMillisecondsField, reply.DurationMillisecondsField];
        var declared = fields.OfType<string>().ToArray();
        return NonBlankText(reply.StatusField, 128) && declared.All(field => NonBlankText(field, 128) && field is not ("id" or "error")) &&
            declared.Distinct(StringComparer.Ordinal).Count() == declared.Length &&
            (!transportMapped || TransportStates.All(value => values.ContainsValue(value)));
    }

    private static bool IsPlaceholder(string value) => value is CliArgumentToken.MediaPath or CliArgumentToken.PositionMilliseconds;
    private static bool IsArguments(ImmutableArray<string> args) => !args.IsDefault && args.Length <= 64 &&
        args.All(arg => arg is { Length: <= 8192 } && !arg.Contains('\0'));
    private static bool NonBlankText(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
}
