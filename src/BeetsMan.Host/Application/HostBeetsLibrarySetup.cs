using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Describes staged native artifacts pinned before installation.</summary>
/// <remarks>2026-11-25: Bind both file identities to the durable setup intent for FR-BEETS-001#ac-open and ac-recover.</remarks>
internal sealed record PreparedBeetsLibrary(Guid StageId, string CatalogHash, string ConfigurationHash);

/// <summary>Records the exact authorized setup attempt and its current recoverable stage.</summary>
internal sealed record BeetsSetupLease(Guid Origin, Guid Operation, string Fingerprint, PreparedBeetsLibrary? Prepared);

/// <summary>Reports the host setup revision and the authorized caller's access.</summary>
internal sealed record BeetsSetupRead(long Revision, bool CanEdit, BeetsSetupLease? Pending, HostBeetsSettings? SavedConfiguration);

/// <summary>Contains the admitted native setup or its original committed response.</summary>
internal sealed record BeetsSetupAdmission(BeetsSetupLease? Lease, HostBeetsLibraryState? Receipt);

/// <summary>Persists authorized setup intents, native checkpoints, and exact receipts.</summary>
/// <remarks>2026-11-25: Keep setup writes scoped to registered desktops for FR-BEETS-001#ac-open.</remarks>
internal interface IHostBeetsSetupStore
{
    Task<HostOutcome<BeetsSetupRead>> ReadBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller, CancellationToken token);
    Task<HostOutcome<BeetsSetupAdmission>> ReserveBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        InitializeHostBeetsRequest request, bool destinationExists, CancellationToken token);
    Task<HostOutcome<BeetsSetupLease>> PinBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        BeetsSetupLease lease, PreparedBeetsLibrary files, CancellationToken token);
    Task<HostOutcome<HostBeetsLibraryState>> CompleteBeetsSetupAsync(Guid library, AuthenticatedHostInstance caller,
        BeetsSetupLease lease, CancellationToken token);
    Task<HostOutcome<HostBeetsConfiguration?>> CheckBeetsConfigurationAsync(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, CancellationToken token);
    Task<HostOutcome<HostBeetsConfiguration>> CommitBeetsConfigurationAsync(Guid library, AuthenticatedHostInstance caller,
        SaveHostBeetsConfigurationRequest request, ImmutableArray<string> availablePluginIds, CancellationToken token);
    Task<HostOutcome<BeetsNativeConfiguration>> ReadBeetsNativeConfigurationAsync(Guid library, CancellationToken token);
}

/// <summary>Inspects and explicitly provisions the host-configured native library.</summary>
/// <remarks>2026-11-25: Keep process and file effects outside LiteDB transactions for FR-BEETS-001#ac-open and ac-recover.</remarks>
internal interface IBeetsLibrarySetupFiles
{
    HostBeetsSettings Settings { get; }
    bool CanInitialize { get; }
    ImmutableArray<string> AvailablePluginIds { get; }
    bool CanReviewSuggestedMatches { get; }
    HostOutcome<BeetsLibraryPresence> Inspect();
    Task<HostOutcome<PreparedBeetsLibrary>> PrepareAsync(CancellationToken token);
    Task<HostOutcome<bool>> InstallAsync(PreparedBeetsLibrary prepared, CancellationToken token);
    Task<HostOutcome<bool>> ProbePluginsAsync(HostBeetsSettings profile, CancellationToken token);
}

/// <summary>Identifies which of the provisioned paths already exists and whether the catalog is recognizable.</summary>
internal sealed record BeetsLibraryPresence(bool HasCatalog, bool HasConfiguration, bool RecognizedCatalog);

/// <summary>Coordinates viewing and explicit initialization without overwriting an existing library.</summary>
/// <remarks>2026-11-25: Serialize setup and preserve its operation across native preparation and recovery for FR-BEETS-001#ac-open.</remarks>
internal sealed partial class HostBeetsLibrarySetup(IHostBeetsSetupStore store, IBeetsLibrarySetupFiles? files)
{
    private readonly SemaphoreSlim setup = new(1, 1);

    /// <summary>Reads the scoped catalog state from the registered host.</summary>
    public async Task<HostOutcome<HostBeetsLibraryState>> ReadAsync(Guid library, AuthenticatedHostInstance caller, CancellationToken token)
    {
        var read = await store.ReadBeetsSetupAsync(library, caller, token).ConfigureAwait(false);
        if (read is not HostOutcome<BeetsSetupRead>.Success { Value: var state })
            return read.PropagateFailure<HostBeetsLibraryState>();
        if (files is null)
            return new HostOutcome<HostBeetsLibraryState>.Success(new(library, state.Revision, false, false));
        var presence = files.Inspect();
        return presence is HostOutcome<BeetsLibraryPresence>.Success { Value: var found }
            ? new HostOutcome<HostBeetsLibraryState>.Success(new(library, state.Revision,
                state.Pending is null && found.RecognizedCatalog, state.CanEdit && (found.HasCatalog || files.CanInitialize)))
            : presence.PropagateFailure<HostBeetsLibraryState>();
    }

    /// <summary>Reads the host-owned configuration for an active desktop.</summary>
    public async Task<HostOutcome<HostBeetsConfiguration>> ReadConfigurationAsync(Guid library, AuthenticatedHostInstance caller, CancellationToken token)
    {
        var read = await store.ReadBeetsSetupAsync(library, caller, token).ConfigureAwait(false);
        if (read is not HostOutcome<BeetsSetupRead>.Success { Value: var state })
            return read.PropagateFailure<HostBeetsConfiguration>();
        if (files is null)
            return new HostOutcome<HostBeetsConfiguration>.Unavailable("beets-library-unconfigured");
        if (state.SavedConfiguration is { } saved && !BeetsConfigurationValues.HasSamePaths(saved, files.Settings))
            return new HostOutcome<HostBeetsConfiguration>.Unavailable("beets-library-configuration-scope-changed");
        return new HostOutcome<HostBeetsConfiguration>.Success(new(library, state.Revision, state.SavedConfiguration ?? files.Settings, state.CanEdit)
            { AvailablePluginIds = files.AvailablePluginIds });
    }

    /// <summary>Initializes the host-provisioned paths once and recovers the same operation on retry.</summary>
    public async Task<HostOutcome<HostBeetsLibraryState>> InitializeAsync(Guid library, AuthenticatedHostInstance caller,
        InitializeHostBeetsRequest request, CancellationToken token)
    {
        if (caller.InstanceId != request.OriginInstanceId || !caller.CanManageRegistrations)
            return new HostOutcome<HostBeetsLibraryState>.Forbidden();
        if (request.OperationId == Guid.Empty || request.ExpectedRevision < 0 || request.Settings is null)
            return new HostOutcome<HostBeetsLibraryState>.Rejected("invalid-beets-library-initialization");
        var authorized = await store.ReadBeetsSetupAsync(library, caller, token).ConfigureAwait(false);
        if (authorized is not HostOutcome<BeetsSetupRead>.Success)
            return authorized.PropagateFailure<HostBeetsLibraryState>();
        if (files is null)
            return new HostOutcome<HostBeetsLibraryState>.Unavailable("beets-library-unconfigured");
        if (!Matches(request.Settings, files.Settings))
            return new HostOutcome<HostBeetsLibraryState>.Rejected("beets-library-destination-unapproved");
        if (!files.CanInitialize)
            return new HostOutcome<HostBeetsLibraryState>.Rejected("beets-library-initialization-disabled");

        try
        {
            await setup.WaitAsync(token).ConfigureAwait(false);
            try { return await InitializeOnLaneAsync(library, caller, request, token).ConfigureAwait(false); }
            finally { setup.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new HostOutcome<HostBeetsLibraryState>.Cancelled();
        }
    }

    private async Task<HostOutcome<HostBeetsLibraryState>> InitializeOnLaneAsync(Guid library, AuthenticatedHostInstance caller,
        InitializeHostBeetsRequest request, CancellationToken token)
    {
        var presence = files!.Inspect();
        if (presence is not HostOutcome<BeetsLibraryPresence>.Success { Value: var current })
            return presence.PropagateFailure<HostBeetsLibraryState>();
        var reserved = await store.ReserveBeetsSetupAsync(library, caller, request with { Settings = files.Settings },
            current.HasCatalog || current.HasConfiguration, token).ConfigureAwait(false);
        if (reserved is not HostOutcome<BeetsSetupAdmission>.Success { Value: var admission })
            return reserved.PropagateFailure<HostBeetsLibraryState>();
        if (admission.Receipt is { } previous)
            return new HostOutcome<HostBeetsLibraryState>.Success(previous);
        var lease = admission.Lease ?? throw new InvalidOperationException("The admitted beets setup had no lease.");
        if (lease.Prepared is null)
        {
            var prepared = await files.PrepareAsync(token).ConfigureAwait(false);
            if (prepared is not HostOutcome<PreparedBeetsLibrary>.Success { Value: var artifacts })
                return prepared.PropagateFailure<HostBeetsLibraryState>();
            var pinned = await store.PinBeetsSetupAsync(library, caller, lease, artifacts, token).ConfigureAwait(false);
            if (pinned is not HostOutcome<BeetsSetupLease>.Success { Value: var accepted })
                return pinned.PropagateFailure<HostBeetsLibraryState>();
            lease = accepted;
        }

        var installed = await files.InstallAsync(lease.Prepared!, token).ConfigureAwait(false);
        if (installed is not HostOutcome<bool>.Success)
            return installed.PropagateFailure<HostBeetsLibraryState>();
        return await store.CompleteBeetsSetupAsync(library, caller, lease, token).ConfigureAwait(false);
    }

    private static bool Matches(HostBeetsSettings submitted, HostBeetsSettings approved) =>
        string.Equals(submitted.CatalogPath, approved.CatalogPath, StringComparison.Ordinal) &&
        string.Equals(submitted.ConfigurationPath, approved.ConfigurationPath, StringComparison.Ordinal) &&
        string.Equals(submitted.SourceMusicRoot, approved.SourceMusicRoot, StringComparison.Ordinal) &&
        submitted.NamingTemplate == approved.NamingTemplate && submitted.ImportDefaults is { } defaults &&
        defaults.Count == approved.ImportDefaults.Count && approved.ImportDefaults.All(item =>
            defaults.TryGetValue(item.Key, out var value) && item.Value == value) &&
        !submitted.EnabledPluginIds.IsDefault && submitted.EnabledPluginIds.SequenceEqual(approved.EnabledPluginIds);
}
