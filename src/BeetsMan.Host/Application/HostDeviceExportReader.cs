using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Reads authorized layout and plan projections from one host store.</summary>
/// <remarks>2026-11-24: Scope read entry points for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
public interface IHostDeviceExportQueryStore
{
    /// <summary>Checks whether the active caller can use the requested Android export.</summary>
    Task<HostOutcome<RegisteredAndroidExport>> ReadExportRegistrationAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid deviceId, CancellationToken token);

    /// <summary>Reads the active profile version and whether the device has previously published.</summary>
    /// <remarks>2026-11-24: Expose committed rather than tentative scope for FR-FILESYNC-001#ac-layouts.</remarks>
    Task<HostOutcome<(DeviceLayoutAssignment? Profile, bool HasPublished)>> ReadExportAssignmentAsync(Guid libraryId,
        AuthenticatedHostInstance caller, Guid deviceId, CancellationToken token);

    /// <summary>Reads the persisted plan, ordered entries, and implicit assignment from the same snapshot.</summary>
    /// <remarks>2026-11-24: Preserve the initial pairing choice for FR-FILESYNC-001#ac-layouts.</remarks>
    Task<HostOutcome<(DeviceExportPlan? Plan, DeviceLayoutAssignment? Assignment)>> FindExportPlanAsync(Guid libraryId,
        AuthenticatedHostInstance caller, DeviceExportPlanLookup lookup, CancellationToken token);
}

/// <summary>Returns the registered device's layout options and its selected staged plan.</summary>
/// <remarks>2026-11-24: Restore the staged list for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
public sealed class HostDeviceExportReader(IHostDeviceExportQueryStore store, IEnumerable<AndroidLayoutProfile> layouts)
{
    private readonly ImmutableDictionary<string, AndroidLayoutProfile> profiles = layouts.ToImmutableDictionary(profile => profile.Id, StringComparer.Ordinal);

    /// <summary>Returns the available profiles after checking the selected Android registration.</summary>
    public async Task<HostOutcome<DeviceLayoutChoices>> ReadLayoutsAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid deviceId, CancellationToken token)
    {
        var assignment = await store.ReadExportAssignmentAsync(libraryId, caller, deviceId, token).ConfigureAwait(false);
        if (assignment is not HostOutcome<(DeviceLayoutAssignment? Profile, bool HasPublished)>.Success { Value: var active })
            return assignment.PropagateFailure<DeviceLayoutChoices>();
        var items = profiles.Values.OrderBy(profile => profile.Id != AndroidLayoutProfile.DefaultId)
            .ThenBy(profile => profile.Id, StringComparer.Ordinal).Select(profile => new DeviceLayoutChoice(profile.Id, profile.Version,
                profile.Player, profile.AudioPathTemplate, profile.PlaylistFormat, profile.SuggestedRoot));
        var selected = active.Profile;
        if (selected is null && !active.HasPublished && profiles.TryGetValue(AndroidLayoutProfile.DefaultId, out var defaultProfile))
            selected = new(defaultProfile.Id, defaultProfile.Version);
        return new HostOutcome<DeviceLayoutChoices>.Success(new(deviceId, [.. items]) { ActiveProfile = selected });
    }

    /// <summary>Reads the selected profile or an explicitly named plan without changing staged state.</summary>
    public async Task<HostOutcome<DeviceExportPlan>> ReadPlanAsync(Guid libraryId, AuthenticatedHostInstance caller, DeviceExportPlanLookup lookup,
        CancellationToken token)
    {
        if (lookup.DeviceId == Guid.Empty || lookup.PlanId == Guid.Empty || (lookup.ProfileId is null) != (lookup.ProfileRevision is null) ||
            lookup.ProfileId is not null && (string.IsNullOrWhiteSpace(lookup.ProfileId) || lookup.ProfileRevision is not > 0))
            return new HostOutcome<DeviceExportPlan>.Rejected("invalid-export-target");

        var result = await store.FindExportPlanAsync(libraryId, caller, lookup, token).ConfigureAwait(false);
        if (result is not HostOutcome<(DeviceExportPlan? Plan, DeviceLayoutAssignment? Assignment)>.Success { Value: var selected })
            return result.PropagateFailure<DeviceExportPlan>();
        var (plan, assignment) = selected;
        var selectedId = lookup.ProfileId;
        var expectedRevision = lookup.ProfileRevision;
        if (selectedId is null && plan is not null)
            return new HostOutcome<DeviceExportPlan>.Success(plan);
        if (selectedId is null && lookup.PlanId is null)
        {
            selectedId = assignment?.ProfileId ?? AndroidLayoutProfile.DefaultId;
            expectedRevision = assignment?.Revision;
        }
        if (selectedId is null || !profiles.TryGetValue(selectedId, out var profile))
            return new HostOutcome<DeviceExportPlan>.Rejected("export-profile-unavailable");
        if (expectedRevision is { } expected && expected != profile.Version)
            return new HostOutcome<DeviceExportPlan>.Rejected("export-profile-changed", profile.Version, Conflict: true);
        if (plan is not null && plan.ProfileRevision != profile.Version)
            return new HostOutcome<DeviceExportPlan>.Rejected("export-plan-profile-changed", plan.ProfileRevision, Conflict: true);
        return new HostOutcome<DeviceExportPlan>.Success(plan ?? new(null, null, lookup.DeviceId, profile.Id, profile.Version, []));
    }
}
