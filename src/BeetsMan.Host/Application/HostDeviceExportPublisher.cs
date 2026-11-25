namespace BeetsMan.Host.Application;

/// <summary>Resumes the recorded file work and commits ownership only after verifying the effects.</summary>
/// <remarks>2026-11-24: Reconcile interrupted filesystem publication for FR-FILESYNC-001#ac-safety.</remarks>
public sealed class HostDeviceExportPublisher(IDeviceExportPublicationJournal journal, IHostDeviceExportQueryStore registrations, IDeviceExportStepWriter files)
{
    private readonly SemaphoreSlim lane = new(1, 1);

    /// <summary>Applies an approved reserved publication or returns its original committed receipt.</summary>
    public async Task<HostOutcome<CompletedDeviceExportPublication>> ResumeAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try { return await ResumeOnLaneAsync(libraryId, caller, intent, token).ConfigureAwait(false); }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new HostOutcome<CompletedDeviceExportPublication>.Cancelled(); }
    }

    private async Task<HostOutcome<CompletedDeviceExportPublication>> ResumeOnLaneAsync(Guid libraryId, AuthenticatedHostInstance caller,
        DeviceExportPublicationIntent intent, CancellationToken token)
    {
        var lookup = await journal.FindPublicationAsync(libraryId, caller, intent, token).ConfigureAwait(false);
        if (lookup is not HostOutcome<DeviceExportPublicationReservation?>.Success { Value: var reservation })
            return lookup.PropagateFailure<CompletedDeviceExportPublication>();
        if (reservation is DeviceExportPublicationReservation.Completed complete)
            return new HostOutcome<CompletedDeviceExportPublication>.Success(complete.Receipt);
        if (reservation is not DeviceExportPublicationReservation.Pending { Work: var work })
            return new HostOutcome<CompletedDeviceExportPublication>.Rejected("export-publication-unavailable");

        var ordinal = work.NextStep;
        while (ordinal < work.Operations.Length)
        {
            var current = await registrations.ReadExportRegistrationAsync(libraryId, caller, work.Destination.DeviceId, token).ConfigureAwait(false);
            if (current is not HostOutcome<RegisteredAndroidExport>.Success { Value: var registered })
                return current.PropagateFailure<CompletedDeviceExportPublication>();
            if (registered != work.Destination)
                return new HostOutcome<CompletedDeviceExportPublication>.Rejected("export-registration-changed", registered.Revision, Conflict: true);

            var effect = await files.ApplyAsync(work, ordinal, token).ConfigureAwait(false);
            if (effect is not HostOutcome<bool>.Success { Value: var verified })
                return effect.PropagateFailure<CompletedDeviceExportPublication>();
            if (!verified)
                return new HostOutcome<CompletedDeviceExportPublication>.Rejected("export-file-effect-unverified");
            var recorded = await journal.RecordPublicationStepAsync(libraryId, caller, work.PublicationId, ordinal, token).ConfigureAwait(false);
            if (recorded is not HostOutcome<int>.Success { Value: var next })
                return recorded.PropagateFailure<CompletedDeviceExportPublication>();
            ordinal = next;
        }

        return await journal.CompletePublicationAsync(libraryId, caller, intent, work.PublicationId, token).ConfigureAwait(false);
    }
}
