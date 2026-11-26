using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Publishes the record, operation receipt and peer deliveries in one host transaction.</summary>
/// <remarks>2026-11-25: Bind shared records to FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
internal interface IHostLibraryRecordStore
{
    Task<HostOutcome<LibraryRecordReceipt>> PublishRecordAsync(Guid library, AuthenticatedHostInstance caller,
        PublishLibraryRecordRequest request, CancellationToken token);
}

/// <summary>Validates registered record mutations before they can enter the feed.</summary>
/// <remarks>2026-11-25: Reject local-only preferences on the host for FR-SETTINGS-001#ac-availability.</remarks>
/// <remarks>2026-11-25: Validate the exact shared theme payload for FR-SETTINGS-001#ac-precedence.</remarks>
internal sealed class HostLibraryRecords(IHostLibraryRecordStore store)
{
    /// <summary>Records one exact favorite, play, or eligible default from the registered origin.</summary>
    public Task<HostOutcome<LibraryRecordReceipt>> PublishAsync(Guid library, AuthenticatedHostInstance caller,
        PublishLibraryRecordRequest request, CancellationToken token)
    {
        if (caller.InstanceId != request.OriginInstanceId)
            return Task.FromResult<HostOutcome<LibraryRecordReceipt>>(new HostOutcome<LibraryRecordReceipt>.Forbidden());
        var valid = request.Change switch
        {
            LibraryRecordIntent.Favorite favorite => favorite.TrackId != Guid.Empty && favorite.ExpectedRevision >= 0,
            LibraryRecordIntent.Play play => play.PlayId != Guid.Empty && play.TrackId != Guid.Empty &&
                play.PlayedAtUtc.Offset == TimeSpan.Zero && play.PlayedAtUtc.UtcTicks > 0,
            LibraryRecordIntent.SharedDefault setting => setting.ExpectedRevision >= 0 &&
                SharedLibraryDefaults.IsValid(setting.Key, setting.DurationTicks, setting.Flag, setting.Text),
            _ => false
        };
        return valid && request.OperationId != Guid.Empty ? store.PublishRecordAsync(library, caller, request, token) :
            Task.FromResult<HostOutcome<LibraryRecordReceipt>>(new HostOutcome<LibraryRecordReceipt>.Rejected("invalid-library-record"));
    }
}
