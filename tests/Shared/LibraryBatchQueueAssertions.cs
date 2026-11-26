using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Observes the atomic queue checkpoint and local save boundary.</summary>
/// <remarks>2026-11-24: Verify FR-LIBRARY-002#ac-batch-local and ac-batch-revert and FR-PLAYER-001#ac-queue-autosave.</remarks>
internal sealed record LibraryBatchQueueObservation(string InitialSaveStatus, string SavedInBatchStatus, string RestoredStatus, string RevertedStatus,
    int HostAttemptsBeforeMode, int HostAttemptsInMode, int HostWritesInMode, int ReviewedChanges, Guid[] ReviewedOrder, Guid[] RestoredOrder,
    bool CheckpointAndEditDurable, bool OrdinaryPendingPreserved, string DiscardedReplayCode, Guid[] AfterDisableOperations,
    Guid OrdinaryPendingOperation, int HostWritesAfterDisable, bool FailedCheckpointAddedBatchEntry);

/// <summary>Detects early queue publications, non-atomic staging, and replay of reverted saves.</summary>
internal static class LibraryBatchQueueAssertions
{
    public static void Verify(LibraryBatchQueueObservation result)
    {
        Assert.Equal("Pending", result.InitialSaveStatus);
        Assert.Equal("StagedInBatch", result.SavedInBatchStatus);
        Assert.Equal("StagedInBatch", result.RestoredStatus);
        Assert.Equal("NotRequested", result.RevertedStatus);
        Assert.Equal(1, result.HostAttemptsBeforeMode);
        Assert.Equal(1, result.HostAttemptsInMode);
        Assert.Equal(0, result.HostWritesInMode);
        Assert.Equal(2, result.ReviewedChanges);
        Assert.Equal(4, result.ReviewedOrder.Length);
        Assert.Equal(result.ReviewedOrder, result.RestoredOrder);
        Assert.True(result.CheckpointAndEditDurable);
        Assert.True(result.OrdinaryPendingPreserved);
        Assert.Equal("library-batch-closed", result.DiscardedReplayCode);
        Assert.Equal([result.OrdinaryPendingOperation], result.AfterDisableOperations);
        Assert.Equal(1, result.HostWritesAfterDisable);
        Assert.False(result.FailedCheckpointAddedBatchEntry);
    }
}
