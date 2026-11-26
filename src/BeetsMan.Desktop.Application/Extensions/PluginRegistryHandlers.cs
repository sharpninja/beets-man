using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Describes the verified local archive and the exact version eligible for desktop trust.</summary>
/// <remarks>2026-11-25: Bind identity, protocol range, entry point and declared contributions for FR-PLUGINS-001#ac-manage.</remarks>
public sealed record PluginPackage(string PluginId, string Version, int MinimumProtocol, int MaximumProtocol, string EntryPoint,
    ImmutableArray<PluginContribution> Contributions, string ArchiveSha256)
{
    public const int HostProtocol = 1;
    public bool Compatible => MinimumProtocol <= HostProtocol && MaximumProtocol >= HostProtocol;
}

/// <summary>Records local trust and enablement for one exact verified package.</summary>
public sealed record InstalledPluginPackage(PluginPackage Package, PluginTrust Trust, bool Enabled);

/// <summary>Captures all registered versions and the desktop-local transaction revision.</summary>
public sealed record PluginRegistrySnapshot(long Revision, ImmutableArray<InstalledPluginPackage> Versions);

/// <summary>Resolves the acquired local selection to validated, privately stored package bytes.</summary>
public interface IPluginPackageIntake
{
    Task<Outcome<PluginPackage>> StageAsync(Guid selectionToken, CancellationToken token);
}

/// <summary>Owns the local registry and commits a typed change in one transaction.</summary>
/// <remarks>2026-11-25: Store trust outside replicated preference state for FR-PLUGINS-001#ac-manage.</remarks>
public interface IPluginRegistryStore
{
    Task<Outcome<PluginRegistrySnapshot>> ReadPluginsAsync(CancellationToken token);
    Task<Outcome<PluginRegistrySnapshot>> EditPluginsAsync(Func<PluginRegistrySnapshot, Outcome<PluginRegistrySnapshot>> edit, CancellationToken token);
}

/// <summary>Registers ordered version updates and local trust transitions through CQRS.</summary>
/// <remarks>2026-11-25: Implement local installation, trust, activation intent and removal for FR-PLUGINS-001#ac-manage.</remarks>
/// <remarks>2026-11-25: Reconcile the committed registry with the worker supervisor for FR-PLUGINS-001#ac-recovery.</remarks>
public sealed partial class PluginRegistryHandlers(IPluginRegistryStore store, IPluginPackageIntake intake, PluginWorkerSupervisor? supervisor = null) :
    IQueryHandler<GetPluginState, PluginState>, ICommandHandler<InstallPluginPackage, PluginState>,
    ICommandHandler<SetPluginTrust, PluginState>, ICommandHandler<SetPluginEnabled, PluginState>, ICommandHandler<RemovePlugin, PluginState>,
    ICommandHandler<RestartPluginWorker, PluginState>
{
    private const int MaximumPackages = 64;

    /// <summary>Registers the desktop-local package management requests.</summary>
    public RequestDispatcher.Builder Register(RequestDispatcher.Builder builder) => builder.RegisterQuery<GetPluginState, PluginState>(this)
        .RegisterCommand<InstallPluginPackage, PluginState>(this).RegisterCommand<SetPluginTrust, PluginState>(this)
        .RegisterCommand<SetPluginEnabled, PluginState>(this).RegisterCommand<RemovePlugin, PluginState>(this)
        .RegisterCommand<RestartPluginWorker, PluginState>(this);

    /// <inheritdoc />
    public async Task<Outcome<PluginState>> HandleAsync(GetPluginState query, RequestContext context, CancellationToken token)
    {
        if (query.PluginId is null ? query.Version is not null : !IsIdentifier(query.PluginId) ||
            query.Version is not null && !IsVersion(query.Version))
            return Invalid<PluginState>("invalid-plugin-version");
        var result = await store.ReadPluginsAsync(token).ConfigureAwait(false);
        return Project(result, query.PluginId, query.Version);
    }

    /// <inheritdoc />
    public async Task<Outcome<PluginState>> HandleAsync(InstallPluginPackage command, CommandContext context, CancellationToken token)
    {
        var staged = await intake.StageAsync(command.PackageSelectionToken, token).ConfigureAwait(false);
        if (staged is not Outcome<PluginPackage>.Success loaded)
            return CopyFailure<PluginPackage>(staged);
        var package = loaded.Value;
        return Project(await store.EditPluginsAsync(current =>
        {
            var found = current.Versions.FirstOrDefault(item => SameVersion(item, package.PluginId, package.Version));
            if (found is not null)
                return found.Package.ArchiveSha256 == package.ArchiveSha256 ? Success(current) :
                    new Outcome<PluginRegistrySnapshot>.Conflict("plugin-version-content-changed", current.Revision);
            return current.Versions.Length >= MaximumPackages ? Invalid<PluginRegistrySnapshot>("plugin-registry-full") :
                Success(current with { Versions = current.Versions.Add(new(package, PluginTrust.Untrusted, false)) });
        }, token).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task<Outcome<PluginState>> HandleAsync(SetPluginTrust command, CommandContext context, CancellationToken token) =>
        !Enum.IsDefined(command.Trust) ? Task.FromResult(Invalid<PluginState>("invalid-plugin-trust")) :
        EditVersionAsync(command.PluginId, command.Version, (current, item) =>
        {
            if (command.Trust == PluginTrust.Local && !item.Package.Compatible)
                return Invalid<PluginRegistrySnapshot>("plugin-protocol-incompatible");
            if (item.Trust == command.Trust)
                return Success(current);
            return Success(current with { Versions = current.Versions.Replace(item,
                item with { Trust = command.Trust, Enabled = command.Trust == PluginTrust.Local && item.Enabled }) });
        }, token);

    /// <inheritdoc />
    public Task<Outcome<PluginState>> HandleAsync(SetPluginEnabled command, CommandContext context, CancellationToken token) =>
        EditVersionAsync(command.PluginId, command.Version, (current, item) =>
        {
            if (command.Enabled && (!item.Package.Compatible || item.Trust != PluginTrust.Local))
                return Invalid<PluginRegistrySnapshot>(item.Package.Compatible ? "plugin-local-trust-required" : "plugin-protocol-incompatible");
            if (item.Enabled == command.Enabled)
                return Success(current);
            var versions = current.Versions.Replace(item, item with { Enabled = command.Enabled });
            if (command.Enabled)
                versions = [.. versions.Select(candidate => candidate.Package.PluginId == command.PluginId && candidate.Package.Version != command.Version
                    && candidate.Enabled ? candidate with { Enabled = false } : candidate)];
            return Success(current with { Versions = versions });
        }, token);

    /// <inheritdoc />
    public Task<Outcome<PluginState>> HandleAsync(RemovePlugin command, CommandContext context, CancellationToken token) =>
        EditVersionAsync(command.PluginId, command.Version, (current, item) => Success(current with { Versions = current.Versions.Remove(item) }), token);

    /// <inheritdoc />
    public async Task<Outcome<PluginState>> HandleAsync(RestartPluginWorker command, CommandContext context, CancellationToken token)
    {
        if (!IsIdentifier(command.PluginId) || !IsVersion(command.Version))
            return Invalid<PluginState>("invalid-plugin-version");
        if (supervisor is null)
            return Invalid<PluginState>("plugin-workers-disabled");
        var result = await store.ReadPluginsAsync(token).ConfigureAwait(false);
        return result is Outcome<PluginRegistrySnapshot>.Success loaded
            ? supervisor.Restart(loaded.Value, command.PluginId, command.Version) : CopyFailure(result);
    }

    /// <summary>Checks the canonical path-independent identity shared by packages and commands.</summary>
    public static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 96 } && IdentifierPattern().IsMatch(value);

    /// <summary>Checks a three-part version with an optional prerelease suffix.</summary>
    public static bool IsVersion(string? value) => value is { Length: > 0 and <= 64 } && VersionPattern().IsMatch(value);

    private async Task<Outcome<PluginState>> EditVersionAsync(string id, string version,
        Func<PluginRegistrySnapshot, InstalledPluginPackage, Outcome<PluginRegistrySnapshot>> edit, CancellationToken token)
    {
        if (!IsIdentifier(id) || !IsVersion(version))
            return Invalid<PluginState>("invalid-plugin-version");
        var result = await store.EditPluginsAsync(current => current.Versions.FirstOrDefault(item => SameVersion(item, id, version)) is { } found
            ? edit(current, found) : Invalid<PluginRegistrySnapshot>("plugin-version-not-found"), token).ConfigureAwait(false);
        return Project(result);
    }

    private Outcome<PluginState> Project(Outcome<PluginRegistrySnapshot> outcome, string? id = null, string? version = null)
    {
        if (outcome is not Outcome<PluginRegistrySnapshot>.Success success)
            return CopyFailure<PluginRegistrySnapshot>(outcome);
        if (supervisor is not null)
            return new Outcome<PluginState>.Success(supervisor.Observe(success.Value, id, version));
        return new Outcome<PluginState>.Success(new(success.Value.Revision,
            [.. success.Value.Versions.Where(item => (id is null || item.Package.PluginId == id) &&
                    (version is null || item.Package.Version == version)).OrderBy(item => item.Package.PluginId, StringComparer.Ordinal)
                .ThenBy(item => item.Package.Version, StringComparer.Ordinal)
                .Select(item => new PluginInfo(item.Package.PluginId, item.Package.Version, item.Trust, item.Enabled, PluginWorkerStatus.Stopped,
                    [], Compatible: item.Package.Compatible))]));
    }

    private static bool SameVersion(InstalledPluginPackage item, string id, string version) => item.Package.PluginId == id && item.Package.Version == version;
    private static Outcome<PluginRegistrySnapshot> Success(PluginRegistrySnapshot snapshot) => new Outcome<PluginRegistrySnapshot>.Success(snapshot);
    private static Outcome<T> Invalid<T>(string code) => new Outcome<T>.Invalid([new(code)]);

    private static Outcome<PluginState> CopyFailure<T>(Outcome<T> result) => result switch
    {
        Outcome<T>.Invalid invalid => new Outcome<PluginState>.Invalid(invalid.Issues),
        Outcome<T>.Conflict conflict => new Outcome<PluginState>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<T>.DependencyFailure failure => new Outcome<PluginState>.DependencyFailure(failure.Code, failure.Retryable),
        _ => new Outcome<PluginState>.Cancelled()
    };

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[a-z0-9]+(?:[.-][a-z0-9]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();
}
