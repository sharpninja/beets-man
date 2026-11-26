using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Identifies a journal entry and the exact native targets approved for it.</summary>
/// <remarks>2026-11-24: Preserve review order and native identity for FR-LIBRARY-002#ac-batch-review and ac-batch-commit.</remarks>
internal sealed record NativeBatchEntry(ApprovedBatchEntry Approved, ImmutableArray<MaintenanceTrack> Targets,
    ImmutableArray<OrganizationCopy> OrganizationCopies = default, OrganizationFileStamp? TaggedFile = null,
    ImmutableArray<AudioRemovalFile> AudioFiles = default, NativeBatchImport? Import = null)
{
    /// <summary>Returns the approved moves for this organization entry in review order.</summary>
    internal ImmutableArray<MaintenancePathChange> Moves => Approved.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.Organize } entry
        ? entry.Plan.Moves : [];

    /// <summary>Identifies catalog-only removal without treating organization as deletion.</summary>
    internal bool RemovesCatalog => Approved.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveCatalog or LibraryMaintenanceKind.RemoveAudio };

    /// <summary>Identifies the independently approved file deletion mode.</summary>
    internal bool DeletesAudio => Approved.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveAudio, AudioDeletionApproved: true };

    /// <summary>Identifies changes that depend on an existing catalog track identity.</summary>
    internal bool RequiresTargets => Approved.Change is ApprovedBatchChange.TrackTags or ApprovedBatchChange.Maintenance;
}

/// <summary>Captures the durable checkpoint for a reviewed batch on the serialized native lane.</summary>
internal sealed record NativeBatchWork(ApprovedLibraryBatchStatus Status, ImmutableArray<NativeBatchEntry> Entries,
    int NextPosition, bool EntryAttempted)
{
    /// <summary>Checks whether a later step relies on this tag write's verified file bytes.</summary>
    internal bool NeedsTagFingerprint(Guid track, int position, bool organized) => organized || Entries.Skip(position)
        .Any(entry => entry.DeletesAudio && entry.Targets.Any(target => target.TrackId == track));
}

/// <summary>Persists native execution fences and atomically publishes the whole batch.</summary>
internal interface INativeBatchWorkStore
{
    Task<HostOutcome<NativeBatchWork?>> ReadNextNativeBatchAsync(Guid library, CancellationToken token);
    /// <summary>Saves verified matched copies under their accepted batch entry before any native effect.</summary>
    Task<HostOutcome<bool>> SaveNativeBatchImportPreparationAsync(NativeBatchWork work, NativeBatchImport import, PreparedImportAlbum manifest, CancellationToken token);
    Task<HostOutcome<bool>> BeginNativeBatchAsync(NativeBatchWork work, string? rejection, CancellationToken token);
    Task<HostOutcome<bool>> MarkNativeBatchEntryAttemptedAsync(NativeBatchWork work, CancellationToken token);
    Task<HostOutcome<bool>> MarkBatchOrganizationAttemptedAsync(NativeBatchWork work, ImmutableArray<OrganizationCopy> copies, CancellationToken token);
    Task<HostOutcome<bool>> AdvanceNativeBatchEntryAsync(NativeBatchWork work, CancellationToken token, OrganizationFileStamp? taggedFile = null);
    /// <summary>Saves an observed album together with advancing the execution cursor.</summary>
    Task<HostOutcome<bool>> RecordNativeBatchImportAsync(NativeBatchWork work, ReconciledImportAlbum album, CancellationToken token);
    /// <summary>Clears an import fence only after the adapter proves it never started the command.</summary>
    Task<HostOutcome<bool>> ResetNativeBatchImportAttemptAsync(NativeBatchWork work, CancellationToken token);
    Task<HostOutcome<bool>> PublishNativeBatchAsync(NativeBatchWork work, CancellationToken token);
}

/// <summary>Reconciles native writes in review order and keeps them private until whole-batch publication.</summary>
/// <remarks>2026-11-24: Recover mixed tag, catalog-only removal, and playlist reviews for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Resolve later edits against approved organization destinations for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Retain the separately reviewed audio deletion path for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Include explicitly decided guided imports for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal sealed partial class NativeBatchWorker(Guid library, INativeBatchWorkStore store, IBeetsTrackTags tags, IBeetsCatalogRemoval catalog,
    IBeetsOrganization organization, IAudioRemovalFiles audio, Func<Guid, IBeetsGuidedImport> imports, Func<Guid, IImportFileStage> stages)
{
    /// <summary>Runs one durable step, or reports that the native lane is available for other work.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var selected = await store.ReadNextNativeBatchAsync(library, token).ConfigureAwait(false);
        if (selected is not HostOutcome<NativeBatchWork?>.Success { Value: var work })
            return selected.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);

        if (work.Status.Phase == BatchPublicationPhase.Accepted)
        {
            var preflight = await CheckBaselineAsync(work, token).ConfigureAwait(false);
            if (preflight is HostOutcome<bool>.Rejected rejected)
                return await store.BeginNativeBatchAsync(work, rejected.Code, token).ConfigureAwait(false);
            if (preflight is not HostOutcome<bool>.Success)
                return preflight;
            var prepared = await PrepareImportsAsync(work, token).ConfigureAwait(false);
            return prepared is HostOutcome<bool>.Success { Value: true }
                ? await store.BeginNativeBatchAsync(work, null, token).ConfigureAwait(false) : prepared;
        }

        if (work.NextPosition > work.Entries.Length)
        {
            var verified = await CheckFinalStateAsync(work, token).ConfigureAwait(false);
            return verified is HostOutcome<bool>.Success ? await store.PublishNativeBatchAsync(work, token).ConfigureAwait(false) : verified;
        }
        var entry = work.Entries[work.NextPosition - 1];
        return entry.Approved.Change switch
        {
            ApprovedBatchChange.TrackTags patch => await RunTagsAsync(work, entry, patch, token).ConfigureAwait(false),
            ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveCatalog } => await RunRemovalAsync(work, entry, token).ConfigureAwait(false),
            ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.RemoveAudio, AudioDeletionApproved: true } =>
                await RunAudioRemovalAsync(work, entry, token).ConfigureAwait(false),
            ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.Organize } => await RunOrganizationAsync(work, entry, token).ConfigureAwait(false),
            ApprovedBatchChange.Playlist => await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false),
            ApprovedBatchChange.Import when entry.Import is { } import => await RunImportAsync(work, import, token).ConfigureAwait(false),
            _ => new HostOutcome<bool>.Unavailable("library-batch-entry-executor-unavailable")
        };
    }

    /// <summary>Verifies the complete baseline before any native entry acquires an execution fence.</summary>
    private async Task<HostOutcome<bool>> CheckBaselineAsync(NativeBatchWork work, CancellationToken token)
    {
        var removed = new HashSet<long>();
        var identities = new Dictionary<long, Guid>();
        var moved = new HashSet<Guid>();
        var destinations = new HashSet<string>(ImportCatalogPublication.PathComparer);
        foreach (var entry in work.Entries)
        {
            if (entry.RequiresTargets && entry.Targets.IsEmpty)
                return Stale();
            foreach (var target in entry.Targets)
            {
                if (removed.Contains(target.BeetsTrackId) || identities.TryGetValue(target.BeetsTrackId, out var owner) && owner != target.TrackId)
                    return Stale();
                identities[target.BeetsTrackId] = target.TrackId;
                if (entry.RemovesCatalog)
                    removed.Add(target.BeetsTrackId);
                var observed = await catalog.ObserveCatalogTrackAsync(target, token).ConfigureAwait(false);
                if (observed is not HostOutcome<NativeCatalogTrackState>.Success { Value: var state })
                    return observed.PropagateFailure<bool>();
                if (state != NativeCatalogTrackState.Present)
                    return Stale();
            }
            if (entry.Approved.Change is ApprovedBatchChange.Maintenance { Plan.Kind: LibraryMaintenanceKind.Organize })
            {
                if (entry.Moves.Any(move => !moved.Add(move.TrackId) || !destinations.Add(move.Destination)))
                    return new HostOutcome<bool>.Rejected("library-batch-organization-path-conflict", Conflict: true);
                var inspected = await OrganizationWorker.InspectCopiesAsync(organization, entry.Targets, entry.Moves, token).ConfigureAwait(false);
                if (inspected is not HostOutcome<ImmutableArray<OrganizationCopy>>.Success)
                    return inspected.PropagateFailure<bool>();
            }
            if (entry.DeletesAudio)
            {
                var inspected = await AudioRemoval.ReadPendingAsync(entry.Targets, entry.AudioFiles, false, catalog, audio, token).ConfigureAwait(false);
                if (inspected is not HostOutcome<ImmutableArray<AudioRemoval.PendingEffect>>.Success)
                    return inspected.PropagateFailure<bool>();
            }
        }
        foreach (var entries in TagEntries(work))
        {
            var first = entries.First();
            var tagWork = TrackWork(work, first.Value, first.Patch);
            var baseline = entries.SelectMany(entry => entry.Patch.Changes.Keys).Distinct(StringComparer.Ordinal)
                .ToImmutableDictionary(key => key, key => tagWork.Baseline.GetValueOrDefault(key), StringComparer.Ordinal);
            var observed = await tags.ObserveAsync(tagWork, token).ConfigureAwait(false);
            if (observed is not HostOutcome<NativeTrackTags>.Success { Value: var actual })
                return observed.PropagateFailure<bool>();
            if (!TrackTagWorker.Matches(actual, baseline))
                return Stale();
        }
        return new HostOutcome<bool>.Success(true);

        static HostOutcome<bool> Stale() => new HostOutcome<bool>.Rejected("library-batch-native-baseline-changed", Conflict: true);
    }

    /// <summary>Reconciles a checkpointed patch against the independent catalog and embedded tags.</summary>
    private async Task<HostOutcome<bool>> RunTagsAsync(NativeBatchWork work, NativeBatchEntry entry, ApprovedBatchChange.TrackTags patch, CancellationToken token)
    {
        var paths = CompletedOrganizationPaths(work);
        var tagWork = TrackWork(work, entry, patch, paths);
        var observed = await tags.ObserveAsync(tagWork, token).ConfigureAwait(false);
        if (observed is not HostOutcome<NativeTrackTags>.Success { Value: var actual })
            return observed.PropagateFailure<bool>();
        var before = tagWork.Changes.Keys.ToImmutableDictionary(field => field, field => tagWork.Baseline.GetValueOrDefault(field), StringComparer.Ordinal);
        var matchesAfter = TrackTagWorker.Matches(actual, tagWork.Changes);
        if ((!work.EntryAttempted || !matchesAfter) && ExpectedTagInput(work, patch.TrackId) is { } expected)
        {
            var input = await organization.ReadFileStampAsync(tagWork.RelativePath, token).ConfigureAwait(false);
            if (input is not HostOutcome<OrganizationFileStamp>.Success { Value: var fingerprint } || fingerprint != expected)
                return new HostOutcome<bool>.Unavailable("library-batch-tag-file-baseline-changed");
        }
        if (matchesAfter && (work.EntryAttempted || TrackTagWorker.Matches(actual, before)))
            return await CompleteTagAsync().ConfigureAwait(false);
        if (!TrackTagWorker.Matches(actual, before))
            return new HostOutcome<bool>.Unavailable("library-batch-tag-reconciliation-incomplete");
        if (!work.EntryAttempted)
            return await store.MarkNativeBatchEntryAttemptedAsync(work, token).ConfigureAwait(false);

        await tags.ApplyAsync(tagWork, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        observed = await tags.ObserveAsync(tagWork, token).ConfigureAwait(false);
        return observed is HostOutcome<NativeTrackTags>.Success { Value: var after } && TrackTagWorker.Matches(after, tagWork.Changes)
            ? await CompleteTagAsync().ConfigureAwait(false)
            : new HostOutcome<bool>.Unavailable("library-batch-tag-reconciliation-incomplete");

        async Task<HostOutcome<bool>> CompleteTagAsync()
        {
            if (!work.NeedsTagFingerprint(patch.TrackId, entry.Approved.Position, paths.ContainsKey(patch.TrackId)))
                return await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false);
            var stamp = await organization.ReadFileStampAsync(tagWork.RelativePath, token).ConfigureAwait(false);
            return stamp is HostOutcome<OrganizationFileStamp>.Success { Value: var tagged }
                ? await store.AdvanceNativeBatchEntryAsync(work, token, tagged).ConfigureAwait(false)
                : stamp.PropagateFailure<bool>();
        }
    }

    /// <summary>Records source bytes after earlier batch edits, then copies or reconciles the approved organization entry.</summary>
    private async Task<HostOutcome<bool>> RunOrganizationAsync(NativeBatchWork work, NativeBatchEntry entry, CancellationToken token)
    {
        if (!work.EntryAttempted)
        {
            var inspected = await OrganizationWorker.InspectCopiesAsync(organization, entry.Targets, entry.Moves, token).ConfigureAwait(false);
            if (inspected is not HostOutcome<ImmutableArray<OrganizationCopy>>.Success { Value: var manifest })
                return inspected.PropagateFailure<bool>();
            if (manifest.Any(copy => ExpectedTagInput(work, copy.Track.TrackId) is { } fingerprint &&
                fingerprint != new OrganizationFileStamp(copy.Size, copy.Sha256)))
                return new HostOutcome<bool>.Unavailable("library-batch-organization-file-baseline-changed");
            return await store.MarkBatchOrganizationAttemptedAsync(work, manifest, token).ConfigureAwait(false);
        }
        var verified = await OrganizationWorker.ReconcileCopiesAsync(organization, entry.OrganizationCopies, token).ConfigureAwait(false);
        return verified is HostOutcome<bool>.Success { Value: true }
            ? await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false) : verified;
    }

    /// <summary>Completes catalog-only removal while retaining every media file.</summary>
    private async Task<HostOutcome<bool>> RunRemovalAsync(NativeBatchWork work, NativeBatchEntry entry, CancellationToken token)
    {
        var remaining = new List<MaintenanceTrack>();
        var paths = CompletedOrganizationPaths(work);
        foreach (var original in entry.Targets)
        {
            var target = AtPath(original, paths);
            var actual = await catalog.ObserveCatalogTrackAsync(target, token).ConfigureAwait(false);
            if (actual is HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Present })
                remaining.Add(target);
            else if (!work.EntryAttempted || actual is not HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Missing })
                return new HostOutcome<bool>.Unavailable("library-batch-removal-reconciliation-incomplete");
        }
        if (remaining.Count == 0)
            return await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false);
        if (!work.EntryAttempted)
            return await store.MarkNativeBatchEntryAttemptedAsync(work, token).ConfigureAwait(false);

        foreach (var target in remaining)
        {
            await catalog.RemoveCatalogTrackAsync(target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (await catalog.ObserveCatalogTrackAsync(target, token).ConfigureAwait(false) is not
                HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Missing })
                return new HostOutcome<bool>.Unavailable("library-batch-removal-reconciliation-incomplete");
        }
        return await store.AdvanceNativeBatchEntryAsync(work, token).ConfigureAwait(false);
    }

    /// <summary>Rechecks the final native effect of every entry before releasing peer publication.</summary>
    private async Task<HostOutcome<bool>> CheckFinalStateAsync(NativeBatchWork work, CancellationToken token)
    {
        var importsCurrent = await VerifyImportsAsync(work, token).ConfigureAwait(false);
        if (importsCurrent is not HostOutcome<bool>.Success)
            return importsCurrent;
        var paths = CompletedOrganizationPaths(work);
        var removed = work.Entries.Where(entry => entry.RemovesCatalog)
            .SelectMany(entry => entry.Targets).ToImmutableArray();
        foreach (var target in removed)
            if (await catalog.ObserveCatalogTrackAsync(AtPath(target, paths), token).ConfigureAwait(false) is not
                HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Missing })
                return new HostOutcome<bool>.Unavailable("library-batch-removal-reconciliation-incomplete");

        var removedIds = removed.Select(track => track.TrackId).ToHashSet();
        foreach (var entries in TagEntries(work).Where(entries => !removedIds.Contains(entries.Key)))
        {
            var first = entries.First();
            var final = entries.SelectMany(entry => entry.Patch.Changes).GroupBy(field => field.Key, StringComparer.Ordinal)
                .ToImmutableDictionary(fields => fields.Key, fields => fields.Last().Value, StringComparer.Ordinal);
            var tagWork = TrackWork(work, first.Value, first.Patch, paths) with { Changes = final };
            if (await tags.ObserveAsync(tagWork, token).ConfigureAwait(false) is not HostOutcome<NativeTrackTags>.Success { Value: var actual } ||
                !TrackTagWorker.Matches(actual, final))
                return new HostOutcome<bool>.Unavailable("library-batch-tag-reconciliation-incomplete");
        }
        var deleted = await VerifyDeletedAudioAsync(work, token).ConfigureAwait(false);
        return deleted is HostOutcome<HashSet<string>>.Success { Value: var deletedPaths }
            ? await VerifyOrganizedFilesAsync(work, removedIds, paths, deletedPaths, token).ConfigureAwait(false)
            : deleted.PropagateFailure<bool>();
    }

    /// <summary>Verifies retained sources and destination bytes after all subsequent tag or catalog removal entries.</summary>
    private async Task<HostOutcome<bool>> VerifyOrganizedFilesAsync(NativeBatchWork work, HashSet<Guid> removed, IReadOnlyDictionary<Guid, string> paths,
        HashSet<string> deletedPaths, CancellationToken token)
    {
        var tagged = work.Entries.Where(entry => entry.TaggedFile is not null).GroupBy(entry => entry.Targets.Single().TrackId)
            .ToDictionary(group => group.Key, group => group.Last().TaggedFile!);
        foreach (var copy in work.Entries.Where(entry => !entry.OrganizationCopies.IsDefaultOrEmpty).SelectMany(entry => entry.OrganizationCopies))
        {
            var original = new OrganizationFileStamp(copy.Size, copy.Sha256);
            if (!deletedPaths.Contains(copy.Track.RelativePath) &&
                (await organization.ReadFileStampAsync(copy.Track.RelativePath, token).ConfigureAwait(false) is not
                    HostOutcome<OrganizationFileStamp>.Success { Value: var sourceFile } || sourceFile != original))
                return new HostOutcome<bool>.Unavailable("library-batch-organization-readback-incomplete");
            var destination = await organization.ReadFileStampAsync(copy.Destination, token).ConfigureAwait(false);
            if (destination is not HostOutcome<OrganizationFileStamp>.Success { Value: var destinationFile } ||
                destinationFile != (tagged.GetValueOrDefault(copy.Track.TrackId) ?? original))
                return new HostOutcome<bool>.Unavailable("library-batch-organization-readback-incomplete");
            if (!removed.Contains(copy.Track.TrackId) && await catalog.ObserveCatalogTrackAsync(AtPath(copy.Track, paths), token).ConfigureAwait(false) is not
                HostOutcome<NativeCatalogTrackState>.Success { Value: NativeCatalogTrackState.Present })
                return new HostOutcome<bool>.Unavailable("library-batch-organization-readback-incomplete");
        }
        return new HostOutcome<bool>.Success(true);
    }

    /// <summary>Resolves the approved paths whose organization checkpoint has already advanced.</summary>
    private static Dictionary<Guid, string> CompletedOrganizationPaths(NativeBatchWork work) => work.Entries.Take(work.NextPosition - 1)
        .SelectMany(entry => entry.Moves).ToDictionary(move => move.TrackId, move => move.Destination);

    /// <summary>Preserves native identity and substitutes only the previously verified destination.</summary>
    private static MaintenanceTrack AtPath(MaintenanceTrack target, IReadOnlyDictionary<Guid, string> paths) =>
        paths.TryGetValue(target.TrackId, out var path) ? target with { RelativePath = path } : target;

    /// <summary>Groups reviewed patches by application track while maintaining journal order.</summary>
    private static IEnumerable<IGrouping<Guid, (NativeBatchEntry Value, ApprovedBatchChange.TrackTags Patch)>> TagEntries(NativeBatchWork work) =>
        work.Entries.Where(entry => entry.Approved.Change is ApprovedBatchChange.TrackTags)
            .Select(entry => (Value: entry, Patch: (ApprovedBatchChange.TrackTags)entry.Approved.Change)).GroupBy(entry => entry.Patch.TrackId);

    /// <summary>Adapts the reviewed change to the existing native tag port without queuing an ordinary job.</summary>
    private static TrackTagWork TrackWork(NativeBatchWork work, NativeBatchEntry entry, ApprovedBatchChange.TrackTags patch,
        IReadOnlyDictionary<Guid, string>? paths = null)
    {
        var target = entry.Targets.Single();
        if (paths is not null)
            target = AtPath(target, paths);
        return new(new(work.Status.OperationId, work.Status.LibraryId, work.Status.BatchRevision, ImportJobPhase.Running),
            patch.TrackId, target.TrackRevision, target.BeetsTrackId, target.RelativePath, patch.Before.ToImmutableDictionary(StringComparer.Ordinal),
            patch.Changes.ToImmutableDictionary(StringComparer.Ordinal), work.EntryAttempted);
    }
}
