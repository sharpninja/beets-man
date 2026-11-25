using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Compares a staged export to the registered share and previously published ownership manifest.</summary>
/// <remarks>2026-11-24: Plan adds, moves, removals and exact approval for FR-FILESYNC-001#ac-safety without changing any file.</remarks>
public sealed class DeviceExportPublicationPlanner
{
    /// <summary>Returns an ordered preview, including blocking conflicts, and the exact removal approval when applicable.</summary>
    public HostOutcome<PreparedDeviceExportPublication> Prepare(DeviceExportPlan plan, RegisteredAndroidExport destination,
        PublishedDeviceExportManifest manifest, DeviceExportShareSnapshot share, DeviceExportPublicationMode mode,
        ImmutableArray<DeviceExportFileCandidate> files, ImmutableHashSet<string>? retirePaths = null, bool confirmMoves = false)
    {
        if (plan.PlanId is not { } planId || planId == Guid.Empty || plan.PlanRevision is not > 0 ||
            plan.DeviceId != destination.DeviceId || destination.DeviceId == Guid.Empty || destination.LibraryId == Guid.Empty ||
            destination.Revision <= 0 || string.IsNullOrWhiteSpace(destination.ShareId) || plan.ProfileRevision <= 0 ||
            string.IsNullOrWhiteSpace(plan.ProfileId) || !MatchesScope(destination, manifest.LibraryId, manifest.DeviceId, manifest.ShareId) ||
            !MatchesScope(destination, share.LibraryId, share.DeviceId, share.ShareId) || manifest.Revision < 0 || !Enum.IsDefined(mode))
            return new HostOutcome<PreparedDeviceExportPublication>.Rejected("invalid-export-publication-scope");
        if (manifest.Files.IsDefault || share.Paths.IsDefault || files.IsDefault)
            return new HostOutcome<PreparedDeviceExportPublication>.Rejected("invalid-export-publication-snapshot");

        var pathComparer = destination.CaseSensitivePaths ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var owned = Index(manifest.Files, file => file.Path, pathComparer);
        var observed = Index(share.Paths, file => file.Path, pathComparer);
        if (owned is null || observed is null || manifest.Files.Any(file => !ValidSource(file.Source) || !ValidDigest(file.ContentIdentity)))
            return new HostOutcome<PreparedDeviceExportPublication>.Rejected("invalid-export-publication-snapshot");

        var issues = ImmutableArray.CreateBuilder<DeviceExportPreviewIssue>();
        var desired = new Dictionary<string, DeviceExportFileCandidate>(pathComparer);
        foreach (var file in files)
        {
            if (!ValidPath(file.Path) || !ValidSource(file.Source))
                issues.Add(new("invalid-export-publication-path", file.Path));
            else if (!desired.TryAdd(file.Path, file))
                issues.Add(new("export-destination-collision", file.Path));
        }

        var operations = ImmutableArray.CreateBuilder<DeviceExportFileOperation>();
        var nextOwned = ImmutableArray.CreateBuilder<OwnedDeviceExportFile>();
        var retired = new List<OwnedDeviceExportFile>();
        foreach (var file in manifest.Files.Where(file => !desired.ContainsKey(file.Path)))
        {
            if (mode == DeviceExportPublicationMode.ReconcileLayout && (retirePaths is null || retirePaths.Contains(file.Path)))
                retired.Add(file);
            else
                nextOwned.Add(file);
        }
        var movable = retired.Where(file => MatchesObservedFile(file, observed) && !HasObstructingParent(file.Path, observed))
            .ToLookup(file => file.Source);
        var moved = new HashSet<string>(pathComparer);
        foreach (var file in desired.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (HasObstructingParent(file.Path, observed) || Parents(file.Path).Any(desired.ContainsKey))
            {
                issues.Add(new("export-destination-collision", file.Path));
                continue;
            }

            owned.TryGetValue(file.Path, out var previous);
            observed.TryGetValue(file.Path, out var current);
            if (!file.WriteRequired)
            {
                if (current is not null && current.Kind != DeviceExportPathKind.File)
                    issues.Add(new("export-destination-collision", file.Path));
                else if (previous is { } existing && existing.Source == file.Source && MatchesObservedFile(existing, observed))
                    nextOwned.Add(existing);
                continue;
            }

            if (previous is not null && previous.Source != file.Source ||
                current is not null && (current.Kind != DeviceExportPathKind.File ||
                !SameDigest(current.ContentIdentity, file.ContentIdentity) && previous is null))
            {
                issues.Add(new("export-destination-collision", file.Path));
                continue;
            }
            if (previous is not null && current is not null && !MatchesObservedFile(previous, observed))
            {
                issues.Add(new("export-owned-content-changed", file.Path));
                continue;
            }
            if (!file.SourceReadable || !ValidDigest(file.ContentIdentity))
            {
                issues.Add(new("export-source-unavailable", file.Path));
                continue;
            }

            var next = new OwnedDeviceExportFile(file.Source, current?.Path ?? file.Path, file.ContentIdentity!.ToUpperInvariant());
            if (current is not null && SameDigest(current.ContentIdentity, next.ContentIdentity))
            {
                // Matching foreign content can be reused without acquiring permission to overwrite or remove it.
                if (previous is not null)
                    nextOwned.Add(next);
                continue;
            }

            nextOwned.Add(next);
            if (previous is not null && current is not null)
            {
                operations.Add(new(DeviceExportOperationKind.Replace, previous, next));
                continue;
            }
            var candidates = movable[file.Source].Where(item => !moved.Contains(item.Path) && SameDigest(item.ContentIdentity, next.ContentIdentity)).Take(2).ToArray();
            if (candidates.Length == 1)
            {
                moved.Add(candidates[0].Path);
                operations.Add(new(DeviceExportOperationKind.Move, candidates[0], next));
            }
            else
                operations.Add(new(DeviceExportOperationKind.Add, null, next));
        }

        foreach (var file in retired.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (moved.Contains(file.Path) || !observed.ContainsKey(file.Path))
                continue;
            if (!MatchesObservedFile(file, observed) || HasObstructingParent(file.Path, observed))
                issues.Add(new("export-owned-content-changed", file.Path));
            else
                operations.Add(new(DeviceExportOperationKind.Remove, file, null));
        }

        var orderedOperations = operations.ToImmutable();
        var removal = issues.Count == 0 ? CreateRemovalProposal(plan, destination, manifest.Revision, orderedOperations, confirmMoves) : null;
        return new HostOutcome<PreparedDeviceExportPublication>.Success(new(plan, destination, manifest.Revision, orderedOperations,
            issues.ToImmutable(), nextOwned.ToImmutable(), removal));
    }

    /// <summary>Returns the pending confirmation, accepts an exact approval, or rejects stale or blocked publication.</summary>
    public static HostOutcome<DeviceExportRemovalProposal?> CheckApproval(PreparedDeviceExportPublication preview, DeviceExportRemovalApproval? approval)
    {
        if (!preview.Issues.IsEmpty)
            return new HostOutcome<DeviceExportRemovalProposal?>.Rejected("export-preview-blocked");
        if (approval is null)
            return new HostOutcome<DeviceExportRemovalProposal?>.Success(preview.RemovalProposal);
        if (preview.RemovalProposal is not { } proposal || approval.ProposalId != proposal.ProposalId ||
            approval.PlanId != proposal.PlanId || approval.PlanRevision != proposal.PlanRevision)
            return new HostOutcome<DeviceExportRemovalProposal?>.Rejected("export-removal-approval-stale", preview.Plan.PlanRevision, Conflict: true);
        return new HostOutcome<DeviceExportRemovalProposal?>.Success(null);
    }

    private static DeviceExportRemovalProposal? CreateRemovalProposal(DeviceExportPlan plan, RegisteredAndroidExport destination,
        long manifestRevision, ImmutableArray<DeviceExportFileOperation> actions, bool confirmMoves)
    {
        var paths = actions.Where(action => action.Kind == DeviceExportOperationKind.Remove || confirmMoves && action.Kind == DeviceExportOperationKind.Move)
            .Select(action => action.Before!.Path).ToImmutableArray();
        if (paths.IsEmpty)
            return null;
        var evidence = new { SchemaVersion = 1, destination.LibraryId, destination.DeviceId, destination.ShareId,
            RegistrationRevision = destination.Revision, plan.PlanId, plan.PlanRevision, plan.ProfileId, plan.ProfileRevision, manifestRevision, actions };
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence));
        return new(new Guid(hash.AsSpan(0, 16)), plan.PlanId!.Value, plan.PlanRevision!.Value, paths);
    }

    private static Dictionary<string, T>? Index<T>(IEnumerable<T> entries, Func<T, string> path, StringComparer comparer)
    {
        var index = new Dictionary<string, T>(comparer);
        foreach (var entry in entries)
        {
            var name = path(entry);
            if (!ValidPath(name) || !index.TryAdd(name, entry))
                return null;
        }
        return index;
    }

    private static bool HasObstructingParent(string path, Dictionary<string, ObservedDeviceExportPath> observed) =>
        Parents(path).Any(parent => observed.TryGetValue(parent, out var item) && item.Kind != DeviceExportPathKind.Directory);

    private static IEnumerable<string> Parents(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            yield return path[..slash];
    }

    private static bool MatchesObservedFile(OwnedDeviceExportFile file, Dictionary<string, ObservedDeviceExportPath> observed) =>
        observed.TryGetValue(file.Path, out var item) && item.Kind == DeviceExportPathKind.File && SameDigest(item.ContentIdentity, file.ContentIdentity);

    private static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool SameDigest(string? actual, string? expected) => actual is not null && expected is not null &&
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static bool ValidSource(ExportContentSource source) => source.EntityId != Guid.Empty && Enum.IsDefined(source.Kind);
    private static bool ValidPath(string path) => PlaylistExportPlanner.TryNormalize(path, out var normalized) && path == normalized;
    private static bool MatchesScope(RegisteredAndroidExport target, Guid library, Guid device, string share) =>
        target.LibraryId == library && target.DeviceId == device && target.ShareId == share;
}
