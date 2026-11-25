using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Configuration;

/// <summary>Contains the typed, revision-bound read of the local configuration file.</summary>
/// <remarks>2026-11-24: Define the startup storage boundary for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed record BootstrapFileRead(string Stamp, bool Exists, BootstrapSettings? Settings, bool NeedsDefaults,
    ImmutableArray<ValidationIssue> Issues);

/// <summary>Reads and atomically writes the one per-user application configuration.</summary>
public interface IBootstrapFile
{
    /// <summary>Gets resolved absolute defaults for this desktop.</summary>
    BootstrapSettings Defaults { get; }

    /// <summary>Reads valid settings, missing defaults, or actionable errors.</summary>
    Task<BootstrapFileRead> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Merges known values while preserving the rest of the document.</summary>
    Task<Outcome<BootstrapFileRead>> WriteAsync(BootstrapSettings settings, string expectedStamp, CancellationToken cancellationToken);
}

/// <summary>Checks the environment and opens a validated desktop database without involving views.</summary>
public interface IBootstrapEnvironment
{
    /// <summary>Checks a resolved path against configured media shares.</summary>
    bool IsShared(string absolutePath);

    /// <summary>Returns whether the proposed desktop database exists.</summary>
    bool DatabaseExists(string absolutePath);

    /// <summary>Returns whether the configured local music root is currently available.</summary>
    bool MusicAvailable(string absolutePath);

    /// <summary>Verifies the existing database schema or initializes a newly approved store.</summary>
    Task<ValidationIssue?> ValidateDatabaseAsync(string absolutePath, bool allowCreate, CancellationToken cancellationToken);
}

/// <summary>Runs bootstrap independently of the normal catalog, plugin and playback services.</summary>
/// <remarks>2026-11-24: Implement startup and repair under FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed class BootstrapHandlers(IBootstrapFile file, IBootstrapEnvironment environment) :
    IQueryHandler<GetBootstrapState, BootstrapState>, ICommandHandler<InitializeDesktop, BootstrapState>,
    ICommandHandler<SaveBootstrapSettings, BootstrapState>, ICommandHandler<RetryDesktopBootstrap, BootstrapState>
{
    private static readonly HashSet<string> Levels = new(["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly SemaphoreSlim lane = new(1, 1);
    private string? lastStamp;
    private long revision;
    private string? activeStamp;
    private BootstrapSettings? activeSettings;
    private DatabaseCreationApproval? proposal;
    private string? proposalStamp;

    /// <summary>Registers the handlers used before the normal application starts.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder
        .RegisterQuery<GetBootstrapState, BootstrapState>(this).RegisterCommand<InitializeDesktop, BootstrapState>(this)
        .RegisterCommand<SaveBootstrapSettings, BootstrapState>(this).RegisterCommand<RetryDesktopBootstrap, BootstrapState>(this);

    /// <inheritdoc />
    public Task<Outcome<BootstrapState>> HandleAsync(GetBootstrapState query, RequestContext context, CancellationToken token) =>
        OnLaneAsync(async () => Success(await InspectAsync(await file.ReadAsync(token).ConfigureAwait(false), false, token).ConfigureAwait(false)), token);

    /// <inheritdoc />
    public Task<Outcome<BootstrapState>> HandleAsync(RetryDesktopBootstrap command, CommandContext context, CancellationToken token) =>
        OnLaneAsync(async () => Success(await InspectAsync(await file.ReadAsync(token).ConfigureAwait(false), true, token).ConfigureAwait(false)), token);

    /// <inheritdoc />
    public Task<Outcome<BootstrapState>> HandleAsync(InitializeDesktop command, CommandContext context, CancellationToken token) =>
        OnLaneAsync(() => SaveAsync(command.Settings, null, command.CreationApproval, true, token), token);

    /// <inheritdoc />
    public Task<Outcome<BootstrapState>> HandleAsync(SaveBootstrapSettings command, CommandContext context, CancellationToken token) =>
        OnLaneAsync(() => SaveAsync(command.Settings, command.ExpectedRevision, command.CreationApproval, false, token), token);

    private async Task<BootstrapState> InspectAsync(BootstrapFileRead read, bool activate, CancellationToken token)
    {
        Observe(read.Stamp);
        if (!read.Issues.IsDefaultOrEmpty)
            return new(revision, BootstrapStatus.RepairRequired, activeSettings, read.Settings ?? file.Defaults, Issues: read.Issues);
        if (!read.Exists)
            return new(revision, BootstrapStatus.FirstRun, activeSettings, file.Defaults);
        var settings = read.Settings!;
        var errors = Validate(settings);
        if (!errors.IsEmpty)
            return new(revision, BootstrapStatus.RepairRequired, activeSettings, settings, Issues: errors);

        if (!environment.DatabaseExists(settings.DatabasePath))
            return RequestApproval(read.Stamp, settings);
        if (activeSettings?.DatabasePath != settings.DatabasePath &&
            await environment.ValidateDatabaseAsync(settings.DatabasePath, false, token).ConfigureAwait(false) is { } storeError)
            return new(revision, BootstrapStatus.RepairRequired, activeSettings, settings, Issues: [storeError]);

        if (activate && activeStamp is null)
        {
            if (read.NeedsDefaults)
            {
                var backfill = await file.WriteAsync(settings, read.Stamp, token).ConfigureAwait(false);
                if (backfill is not Outcome<BootstrapFileRead>.Success saved)
                    return new(revision, BootstrapStatus.RepairRequired, null, settings, Issues: [new("configuration-defaults-write-failed")]);
                read = saved.Value;
                Observe(read.Stamp);
            }
            activeStamp = read.Stamp;
            activeSettings = settings;
        }

        return activeStamp is not null && activeStamp != read.Stamp
            ? new(revision, BootstrapStatus.RestartRequired, activeSettings, settings, Issues: MusicIssues(settings))
            : new(revision, BootstrapStatus.Ready, settings, null, Issues: MusicIssues(settings));
    }

    private async Task<Outcome<BootstrapState>> SaveAsync(BootstrapSettings settings, long? expectedRevision,
        DatabaseCreationApproval? approval, bool initialize, CancellationToken token)
    {
        var read = await file.ReadAsync(token).ConfigureAwait(false);
        Observe(read.Stamp);
        if (expectedRevision.HasValue && expectedRevision != revision)
            return new Outcome<BootstrapState>.Conflict("configuration-changed", revision);
        if (initialize && read.Exists)
            return new Outcome<BootstrapState>.Conflict("configuration-already-exists", revision);
        if (!read.Issues.IsDefaultOrEmpty)
            return new Outcome<BootstrapState>.Invalid(read.Issues);

        var errors = Validate(settings);
        if (!errors.IsEmpty)
            return new Outcome<BootstrapState>.Invalid(errors);

        var exists = environment.DatabaseExists(settings.DatabasePath);
        var standardInitialization = initialize && string.Equals(settings.DatabasePath, file.Defaults.DatabasePath, PathComparison);
        var confirmedCustomPath = approval is not null && approval == proposal && proposalStamp == read.Stamp &&
            string.Equals(approval.DatabasePath, settings.DatabasePath, PathComparison);
        if (!exists && !standardInitialization && !confirmedCustomPath)
            return Success(RequestApproval(read.Stamp, settings));

        if (activeSettings?.DatabasePath != settings.DatabasePath || !exists)
        {
            var issue = await environment.ValidateDatabaseAsync(settings.DatabasePath, allowCreate: !exists, token).ConfigureAwait(false);
            if (issue is not null)
                return new Outcome<BootstrapState>.Invalid([issue]);
        }
        var saved = await file.WriteAsync(settings, read.Stamp, token).ConfigureAwait(false);
        if (saved is not Outcome<BootstrapFileRead>.Success written)
            return ConvertFailure(saved);
        Observe(written.Value.Stamp);
        if (activeSettings is not null)
            return Success(new(revision, BootstrapStatus.RestartRequired, activeSettings, settings, Issues: MusicIssues(settings)));
        activeSettings = settings;
        activeStamp = written.Value.Stamp;
        return Success(new(revision, BootstrapStatus.Ready, activeSettings, null, Issues: MusicIssues(settings)));
    }

    private ImmutableArray<ValidationIssue> Validate(BootstrapSettings settings)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        if (!string.Equals(settings.SettingsPath, file.Defaults.SettingsPath, PathComparison) || !Path.IsPathFullyQualified(settings.SettingsPath) ||
            environment.IsShared(settings.SettingsPath))
            issues.Add(new("invalid-configuration-path", nameof(settings.SettingsPath)));
        if (!Path.IsPathFullyQualified(settings.DatabasePath) || environment.IsShared(settings.DatabasePath) ||
            string.Equals(settings.DatabasePath, settings.SettingsPath, PathComparison))
            issues.Add(new("invalid-database-path", nameof(settings.DatabasePath)));
        if (!Path.IsPathFullyQualified(settings.MusicRoot))
            issues.Add(new("invalid-music-path", nameof(settings.MusicRoot)));
        if (settings.RollingLog is { } rolling && (!Path.IsPathFullyQualified(rolling.Directory) || environment.IsShared(rolling.Directory) || rolling.RetainedFileCount <= 0))
            issues.Add(new("invalid-rolling-log-settings", nameof(settings.RollingLog)));
        if (settings.LogLevels.Any(item => !Levels.Contains(item.Value)))
            issues.Add(new("invalid-log-level", nameof(settings.LogLevels)));
        return issues.ToImmutable();
    }

    private ImmutableArray<ValidationIssue> MusicIssues(BootstrapSettings settings) => environment.MusicAvailable(settings.MusicRoot)
        ? [] : [new("music-root-unavailable", nameof(settings.MusicRoot))];

    private BootstrapState RequestApproval(string stamp, BootstrapSettings settings)
    {
        if (proposal is null || proposalStamp != stamp || !string.Equals(proposal.DatabasePath, settings.DatabasePath, PathComparison))
        {
            proposal = new(Guid.NewGuid(), settings.DatabasePath);
            proposalStamp = stamp;
        }
        return new(revision, BootstrapStatus.CreationApprovalRequired, activeSettings, settings, proposal);
    }

    private void Observe(string stamp)
    {
        if (lastStamp == stamp)
            return;
        lastStamp = stamp;
        revision = checked(revision + 1);
        proposal = null;
        proposalStamp = null;
    }

    private async Task<Outcome<BootstrapState>> OnLaneAsync(Func<Task<Outcome<BootstrapState>>> action, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await action().ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new Outcome<BootstrapState>.Cancelled(); }
        catch (IOException) { return new Outcome<BootstrapState>.DependencyFailure("configuration-storage-unavailable", true); }
        catch (UnauthorizedAccessException) { return new Outcome<BootstrapState>.DependencyFailure("configuration-access-denied", true); }
    }

    private static Outcome<BootstrapState> Success(BootstrapState value) => new Outcome<BootstrapState>.Success(value);

    private Outcome<BootstrapState> ConvertFailure(Outcome<BootstrapFileRead> result) => result switch
    {
        Outcome<BootstrapFileRead>.Conflict conflict => new Outcome<BootstrapState>.Conflict(conflict.Code, revision),
        Outcome<BootstrapFileRead>.Invalid invalid => new Outcome<BootstrapState>.Invalid(invalid.Issues),
        Outcome<BootstrapFileRead>.Cancelled => new Outcome<BootstrapState>.Cancelled(),
        Outcome<BootstrapFileRead>.DependencyFailure dependent => new Outcome<BootstrapState>.DependencyFailure(dependent.Code, dependent.Retryable),
        _ => throw new InvalidOperationException("Expected an unsuccessful configuration write result.")
    };
}
