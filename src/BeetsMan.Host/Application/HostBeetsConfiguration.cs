using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Provides the revision-bound native overlay, with an absent profile representing the provisioned baseline.</summary>
/// <remarks>2026-11-25: Load committed settings at native command admission for FR-BEETS-001#ac-open.</remarks>
internal sealed record BeetsNativeConfiguration(long Revision, HostBeetsSettings? Settings);

/// <summary>Validates the settings that the host can apply to its controlled beets configuration.</summary>
/// <remarks>2026-11-25: Keep native plugin grants and path boundaries in one place for FR-BEETS-001#ac-open.</remarks>
internal static class BeetsConfigurationValues
{
    public const string Behavior = "behavior";
    public const string KeepTags = "keep-tags";
    public const string ReviewSuggestedMatches = "review-suggested-matches";

    /// <summary>Checks the three paths that identify the provisioned library.</summary>
    public static bool HasSamePaths(HostBeetsSettings settings, HostBeetsSettings baseline) =>
        settings.CatalogPath == baseline.CatalogPath && settings.ConfigurationPath == baseline.ConfigurationPath &&
        settings.SourceMusicRoot == baseline.SourceMusicRoot;

    /// <summary>Accepts the default or a bounded relative native naming template.</summary>
    public static bool IsNamingTemplate(string? value) => value is "" || value is { Length: > 0 and <= 512 } &&
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains('\\') && !value.Contains(':') &&
        !value.Any(char.IsControl) && value.Split('/').All(segment => segment is not ("" or "." or ".."));

    /// <summary>Checks a native module ID. The launch owner establishes the allowed subset.</summary>
    public static bool IsPluginId(string? id) => id is { Length: > 0 and <= 64 } && char.IsAsciiLetterLower(id[0]) &&
        id.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_');
}

internal sealed partial class HostBeetsLibrarySetup
{
    /// <summary>Applies the saved settings only for an active Operator and an approved host profile.</summary>
    /// <remarks>2026-11-25: Verify installed native plugins and commit the typed overlay for FR-BEETS-001#ac-open.</remarks>
    public async Task<HostOutcome<HostBeetsConfiguration>> SaveConfigurationAsync(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, CancellationToken token)
    {
        if (!caller.CanManageRegistrations || caller.InstanceId != request.OriginInstanceId)
            return new HostOutcome<HostBeetsConfiguration>.Forbidden();
        if (request.OperationId == Guid.Empty || request.ExpectedRevision < 0 || request.Settings is null)
            return Invalid("invalid-beets-library-configuration");
        var access = await store.ReadBeetsSetupAsync(library, caller, token).ConfigureAwait(false);
        if (access is not HostOutcome<BeetsSetupRead>.Success)
            return access.PropagateFailure<HostBeetsConfiguration>();
        if (files is null)
            return new HostOutcome<HostBeetsConfiguration>.Unavailable("beets-library-unconfigured");
        var settings = request.Settings;
        if (!BeetsConfigurationValues.HasSamePaths(settings, files.Settings))
            return Invalid("beets-library-destination-unapproved");
        if (!BeetsConfigurationValues.IsNamingTemplate(settings.NamingTemplate))
            return Invalid("beets-library-naming-template-invalid");
        if (settings.ImportDefaults is not { Count: 1 } defaults ||
            !defaults.TryGetValue(BeetsConfigurationValues.Behavior, out var behavior) ||
            (behavior != BeetsConfigurationValues.KeepTags &&
                (behavior != BeetsConfigurationValues.ReviewSuggestedMatches || !files.CanReviewSuggestedMatches)))
            return Invalid("beets-library-import-behavior-unsupported");
        if (settings.EnabledPluginIds.IsDefault || settings.EnabledPluginIds.Any(id =>
                !BeetsConfigurationValues.IsPluginId(id) || !files.AvailablePluginIds.Contains(id, StringComparer.Ordinal)) ||
            settings.EnabledPluginIds.Distinct(StringComparer.Ordinal).Count() != settings.EnabledPluginIds.Length)
            return Invalid("beets-library-plugin-unapproved");

        try
        {
            await setup.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var canonical = request with { Settings = settings with
                    { EnabledPluginIds = [.. settings.EnabledPluginIds.Order(StringComparer.Ordinal)] } };
                var check = await store.CheckBeetsConfigurationAsync(library, caller, canonical, token).ConfigureAwait(false);
                if (check is not HostOutcome<HostBeetsConfiguration?>.Success { Value: var prior })
                    return check.PropagateFailure<HostBeetsConfiguration>();
                if (prior is not null)
                    return new HostOutcome<HostBeetsConfiguration>.Success(prior);
                var presence = files.Inspect();
                if (presence is not HostOutcome<BeetsLibraryPresence>.Success { Value: var current })
                    return presence.PropagateFailure<HostBeetsConfiguration>();
                if (!current.RecognizedCatalog)
                    return Invalid("beets-library-not-initialized");
                var probe = await files.ProbePluginsAsync(canonical.Settings, token).ConfigureAwait(false);
                if (probe is not HostOutcome<bool>.Success { Value: var installed })
                    return probe.PropagateFailure<HostBeetsConfiguration>();
                if (!installed)
                    return Invalid("beets-library-plugin-unavailable");
                return await store.CommitBeetsConfigurationAsync(library, caller, canonical, files.AvailablePluginIds, token).ConfigureAwait(false);
            }
            finally { setup.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<HostBeetsConfiguration>.Cancelled();
        }
    }

    private static HostOutcome<HostBeetsConfiguration> Invalid(string code) => new HostOutcome<HostBeetsConfiguration>.Rejected(code);
}
