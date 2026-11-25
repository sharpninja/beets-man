using System.Security.Cryptography;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Projects verified transfer progress and records origin-scoped retries.</summary>
/// <remarks>2026-11-24: Bind the existing FR-FILESYNC-001#ac-safety transfer inspection and retry commands.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string TransferRetryReceipt = "DeviceExportTransferRetried";

    /// <inheritdoc />
    public Task<HostOutcome<DeviceExportTransferCheckpoint>> ReadTransferAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid transferId,
        DateTimeOffset now, CancellationToken token) => OnLaneAsync<DeviceExportTransferCheckpoint>(() =>
        {
            var read = ReadAuthorizedScanRow(libraryId, caller, transferId);
            if (read is not HostOutcome<BsonDocument>.Success { Value: var row })
                return read.PropagateFailure<DeviceExportTransferCheckpoint>();
            var checkpoint = ReadScan(row);
            if (IsTransferPending(checkpoint.State) && ScanScopeFailure(row, checkpoint) is { } changed)
                checkpoint = checkpoint with { State = changed.State, FailureCode = changed.Code };
            return new HostOutcome<DeviceExportTransferCheckpoint>.Success(ProjectTransfer(checkpoint, LeaseActive(row, now.UtcDateTime)));
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<RetryDeviceExportTransferResponse>> RetryTransferAsync(Guid libraryId, AuthenticatedHostInstance caller, Guid transferId,
        RetryDeviceExportTransferRequest request, DateTimeOffset now, CancellationToken token) => ExecuteAsync<RetryDeviceExportTransferResponse>(_ =>
        {
            if (caller.InstanceId != request.OriginInstanceId)
                return new HostOutcome<RetryDeviceExportTransferResponse>.Forbidden();
            var read = ReadAuthorizedScanRow(libraryId, caller, transferId);
            if (read is not HostOutcome<BsonDocument>.Success { Value: var row })
                return read.PropagateFailure<RetryDeviceExportTransferResponse>();
            if (request.OperationId == Guid.Empty || request.ExpectedRevision is <= 0 or long.MaxValue)
                return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected("invalid-export-transfer-retry");

            var id = OperationId(new(caller.InstanceId, request.OperationId));
            var fingerprint = RetryTransferFingerprint(libraryId, transferId, request.ExpectedRevision);
            var receipts = database.GetCollection<BsonDocument>("operation_receipts");
            if (receipts.FindById(id) is { } existing)
            {
                var receipt = existing["Receipt"].AsDocument;
                if (existing["IntentFingerprint"].AsString != fingerprint || receipt["Type"].AsString != TransferRetryReceipt || receipt["Version"].AsInt32 != 1)
                    return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected("operation-intent-mismatch", Conflict: true);
                return new HostOutcome<RetryDeviceExportTransferResponse>.Success(ReadStored<RetryDeviceExportTransferResponse>(receipt["Result"]));
            }

            var checkpoint = ReadScan(row);
            if (checkpoint.Revision != request.ExpectedRevision)
                return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected("export-transfer-changed", checkpoint.Revision, Conflict: true);
            if (LeaseActive(row, now.UtcDateTime))
                return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected("export-transfer-in-progress", checkpoint.Revision, Conflict: true);
            if (!IsTransferPending(checkpoint.State) || checkpoint.FailureCode is null)
                return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected("export-transfer-retry-not-needed");
            if (ScanScopeFailure(row, checkpoint) is { } changed)
                return new HostOutcome<RetryDeviceExportTransferResponse>.Rejected(changed.Code, checkpoint.Revision, Conflict: true);

            UpdateScan(database.GetCollection<BsonDocument>(ExportScanCollection), row, checkpoint.State, null, now.UtcDateTime);
            var result = new RetryDeviceExportTransferResponse(request.OperationId, ProjectTransfer(ReadScan(row), leased: false));
            receipts.Insert(new BsonDocument
            {
                ["_id"] = id, ["OriginInstanceId"] = caller.InstanceId, ["OperationId"] = request.OperationId, ["IntentFingerprint"] = fingerprint,
                ["Receipt"] = new BsonDocument { ["Type"] = TransferRetryReceipt, ["Version"] = 1, ["Result"] = JsonSerializer.Serialize(result) }
            });
            return new HostOutcome<RetryDeviceExportTransferResponse>.Success(result);
        }, token);

    private static bool IsTransferPending(DeviceExportScanState state) => state is DeviceExportScanState.Queued or DeviceExportScanState.AwaitingReceiver;

    private static DeviceExportTransferCheckpoint ProjectTransfer(DeviceExportScanCheckpoint checkpoint, bool leased)
    {
        var phase = checkpoint.State switch
        {
            DeviceExportScanState.Queued when checkpoint.FailureCode == "export-scan-folder-paused" => DeviceExportTransferPhase.Paused,
            DeviceExportScanState.Queued => DeviceExportTransferPhase.Queued,
            DeviceExportScanState.AwaitingReceiver => DeviceExportTransferPhase.AwaitingReceiver,
            DeviceExportScanState.Completed => DeviceExportTransferPhase.Complete,
            _ => DeviceExportTransferPhase.Failed
        };
        return new(checkpoint.TransferId, checkpoint.Destination.MediaShareId, checkpoint.Revision, phase, checkpoint.BytesVerified,
            checkpoint.TotalBytes, checkpoint.FailureCode, !leased && IsTransferPending(checkpoint.State) && checkpoint.FailureCode is not null);
    }

    private static string RetryTransferFingerprint(Guid libraryId, Guid transferId, long revision) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Operation = TransferRetryReceipt, LibraryId = libraryId, TransferId = transferId, Revision = revision })));
}
