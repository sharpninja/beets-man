using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Pins an approved path change and the source bytes that must survive it.</summary>
/// <remarks>2026-11-24: Freeze the exact source identity for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal sealed record OrganizationCopy(MaintenanceTrack Track, string Destination, long Size, string Sha256);

/// <summary>Identifies the verified bytes of an original or organized file.</summary>
/// <remarks>2026-11-24: Preserve file identity through later batch tag writes for FR-LIBRARY-002#ac-batch-commit.</remarks>
internal sealed record OrganizationFileStamp(long Size, string Sha256)
{
    /// <summary>Checks a complete length and SHA-256 stored at the native checkpoint.</summary>
    internal bool IsValid() => Size > 0 && Sha256 is { Length: 64 } && Sha256.All(Uri.IsHexDigit);
}

/// <summary>Holds the approved selection and the verified source manifest across an interrupted run.</summary>
internal sealed record OrganizationWork(ImportJobSnapshot Job, LibraryMaintenancePlan Plan, ImmutableArray<MaintenanceTrack> Tracks,
    ImmutableArray<OrganizationCopy> Copies);

/// <summary>Distinguishes verified native paths from changed identities or conflicting bytes.</summary>
internal enum NativeOrganizationState { Source, Destination, Diverged }

/// <summary>Inspects, verifies and changes the path of one exact native record.</summary>
internal interface IBeetsOrganization
{
    Task<HostOutcome<OrganizationCopy>> InspectAsync(MaintenanceTrack track, MaintenancePathChange move, CancellationToken token);
    Task<HostOutcome<NativeOrganizationState>> ObserveAsync(OrganizationCopy copy, CancellationToken token);
    Task<HostOutcome<bool>> ApplyAsync(OrganizationCopy copy, CancellationToken token);
    /// <summary>Verifies bytes from an exact, ordinary file beneath the music root.</summary>
    Task<HostOutcome<OrganizationFileStamp>> ReadFileStampAsync(string relativePath, CancellationToken token);
}

/// <summary>Owns the organization attempt fence and atomic host catalog publication.</summary>
internal interface IOrganizationWorkStore
{
    Task<HostOutcome<OrganizationWork?>> ReadNextOrganizationAsync(Guid library, CancellationToken token);
    Task<HostOutcome<OrganizationWork?>> BeginOrganizationAsync(OrganizationWork work, ImmutableArray<OrganizationCopy> copies, CancellationToken token);
    Task<HostOutcome<bool>> FailOrganizationAsync(OrganizationWork work, string code, bool retryable, CancellationToken token);
    Task<HostOutcome<bool>> PublishOrganizationAsync(OrganizationWork work, CancellationToken token);
}

/// <summary>Copies the approved sources, reconciles native paths after interruption, and publishes only verified results.</summary>
/// <remarks>2026-11-24: Execute STB18 organization for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal sealed class OrganizationWorker(Guid library, IOrganizationWorkStore store, IBeetsOrganization native)
{
    /// <summary>Processes the next admitted or interrupted organization on the shared native lane.</summary>
    public async Task<HostOutcome<bool>> RunNextAsync(CancellationToken token)
    {
        var selected = await store.ReadNextOrganizationAsync(library, token).ConfigureAwait(false);
        if (selected is not HostOutcome<OrganizationWork?>.Success { Value: var work })
            return selected.PropagateFailure<bool>();
        if (work is null)
            return new HostOutcome<bool>.Success(false);
        if (work.Job.Phase == ImportJobPhase.Accepted)
        {
            var inspected = await InspectCopiesAsync(native, work.Tracks, work.Plan.Moves, token).ConfigureAwait(false);
            if (inspected is not HostOutcome<ImmutableArray<OrganizationCopy>>.Success { Value: var copies })
                return await FailAsync(work, inspected, token).ConfigureAwait(false);
            var begun = await store.BeginOrganizationAsync(work, copies, token).ConfigureAwait(false);
            if (begun is not HostOutcome<OrganizationWork?>.Success { Value: var active })
                return begun.PropagateFailure<bool>();
            if (active is null)
                return new HostOutcome<bool>.Success(true);
            work = active;
        }

        var reconciled = await ReconcileCopiesAsync(native, work.Copies, token).ConfigureAwait(false);
        return reconciled is HostOutcome<bool>.Success { Value: true }
            ? await store.PublishOrganizationAsync(work, token).ConfigureAwait(false)
            : await FailAsync(work, reconciled, token).ConfigureAwait(false);
    }

    /// <summary>Pins source bytes for every approved move before direct or batch execution starts.</summary>
    internal static async Task<HostOutcome<ImmutableArray<OrganizationCopy>>> InspectCopiesAsync(IBeetsOrganization native,
        ImmutableArray<MaintenanceTrack> targets, ImmutableArray<MaintenancePathChange> moves, CancellationToken token)
    {
        if (moves.IsDefaultOrEmpty || !HostLibraryMaintenance.ValidMoves(targets, moves))
            return new HostOutcome<ImmutableArray<OrganizationCopy>>.Rejected("invalid-library-maintenance-paths", Conflict: true);
        var tracks = targets.ToDictionary(track => track.TrackId);
        var copies = ImmutableArray.CreateBuilder<OrganizationCopy>(moves.Length);
        foreach (var move in moves)
        {
            var inspected = await native.InspectAsync(tracks[move.TrackId], move, token).ConfigureAwait(false);
            if (inspected is not HostOutcome<OrganizationCopy>.Success { Value: var copy })
                return inspected.PropagateFailure<ImmutableArray<OrganizationCopy>>();
            copies.Add(copy);
        }
        return new HostOutcome<ImmutableArray<OrganizationCopy>>.Success(copies.MoveToImmutable());
    }

    /// <summary>Recovers a completed copy without rewriting it and verifies the entire manifest.</summary>
    internal static async Task<HostOutcome<bool>> ReconcileCopiesAsync(IBeetsOrganization native, ImmutableArray<OrganizationCopy> copies, CancellationToken token)
    {
        foreach (var copy in copies)
        {
            var before = await native.ObserveAsync(copy, token).ConfigureAwait(false);
            if (before is HostOutcome<NativeOrganizationState>.Success { Value: NativeOrganizationState.Destination })
                continue;
            if (before is not HostOutcome<NativeOrganizationState>.Success { Value: NativeOrganizationState.Source })
                return ReconciliationFailure(before);
            var applied = await native.ApplyAsync(copy, token).ConfigureAwait(false);
            if (applied is not HostOutcome<bool>.Success { Value: true })
                return ReconciliationFailure(applied);
        }
        foreach (var copy in copies)
        {
            var after = await native.ObserveAsync(copy, token).ConfigureAwait(false);
            if (after is not HostOutcome<NativeOrganizationState>.Success { Value: NativeOrganizationState.Destination })
                return ReconciliationFailure(after);
        }
        return new HostOutcome<bool>.Success(true);

        static HostOutcome<bool> ReconciliationFailure<T>(HostOutcome<T> outcome) => outcome is HostOutcome<T>.Success
            ? new HostOutcome<bool>.Unavailable("organization-reconciliation-incomplete") : outcome.PropagateFailure<bool>();
    }

    /// <summary>Retains the manifest on failure and requires new approval for a stale unstarted plan.</summary>
    private Task<HostOutcome<bool>> FailAsync<T>(OrganizationWork work, HostOutcome<T> failure, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return failure switch
        {
            HostOutcome<T>.Rejected rejected => store.FailOrganizationAsync(work, rejected.Code, work.Job.Phase == ImportJobPhase.Running, token),
            HostOutcome<T>.Unavailable unavailable => store.FailOrganizationAsync(work, unavailable.Code, true, token),
            _ => store.FailOrganizationAsync(work, "organization-reconciliation-incomplete", work.Job.Phase == ImportJobPhase.Running, token)
        };
    }
}
