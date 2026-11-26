using BeetsMan.Host.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Drains admitted and recoverable guided imports while the library host is running.</summary>
/// <remarks>2026-11-24: Start the durable worker with the configured beets library for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Run imports and track edits sequentially on the same native library for FR-BEETS-001#ac-edit.</remarks>
/// <remarks>2026-11-24: Recover catalog-only removal on that lane before starting other native operations for FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Keep ordinary native operations off the lane during batch recovery for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Execute approved organization on the same native lane for FR-BEETS-001#ac-edit and ac-recover.</remarks>
/// <remarks>2026-11-24: Offer private guided reviews only after verification for FR-LIBRARY-002#ac-batch-commit.</remarks>
internal sealed class GuidedImportHostedService(GuidedImportWorker worker, TrackTagWorker tags, CatalogRemovalWorker removal, NativeBatchWorker batch,
    OrganizationWorker organization, ApprovedBatchImportReviewWorker batchReviews, ILogger<GuidedImportHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var batched = await batch.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (batched is not HostOutcome<bool>.Success { Value: false })
            {
                if (batched is HostOutcome<bool>.Unavailable unavailableBatch)
                    logger.LogWarning("Approved native batch could not advance. Code {FailureCode}.", unavailableBatch.Code);
                else if (batched is HostOutcome<bool>.Rejected blockedBatch)
                    logger.LogWarning("Approved native batch could not advance. Code {FailureCode}.", blockedBatch.Code);
                if (batched is not HostOutcome<bool>.Success)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                continue;
            }
            var reviewed = await batchReviews.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (reviewed is HostOutcome<bool>.Unavailable unavailableReview)
                logger.LogWarning("Approved batch import review could not advance. Code {FailureCode}.", unavailableReview.Code);
            var removed = await removal.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (removed is HostOutcome<bool>.Unavailable unavailableRemoval)
                logger.LogWarning("Catalog removal queue could not be read. Code {FailureCode}.", unavailableRemoval.Code);
            var organized = await organization.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (organized is HostOutcome<bool>.Unavailable unavailableOrganization)
                logger.LogWarning("Organization queue could not be read. Code {FailureCode}.", unavailableOrganization.Code);
            var result = await worker.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (result is HostOutcome<bool>.Unavailable unavailable)
                logger.LogWarning("Guided import queue could not be read. Code {FailureCode}.", unavailable.Code);
            var edited = await tags.RunNextAsync(stoppingToken).ConfigureAwait(false);
            if (edited is HostOutcome<bool>.Unavailable unavailableTags)
                logger.LogWarning("Track tag queue could not be read. Code {FailureCode}.", unavailableTags.Code);
            if (reviewed is not HostOutcome<bool>.Success { Value: true } && removed is not HostOutcome<bool>.Success { Value: true } &&
                organized is not HostOutcome<bool>.Success { Value: true } &&
                result is not HostOutcome<bool>.Success { Value: true } &&
                edited is not HostOutcome<bool>.Success { Value: true })
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
