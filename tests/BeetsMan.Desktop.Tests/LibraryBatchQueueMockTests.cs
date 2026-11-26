using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Rejects independently scripted queue batch boundary failures before implementation.</summary>
/// <remarks>2026-11-24: Establish the mock gate for TEST-LIBRARY-003 and FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed class LibraryBatchQueueMockTests
{
    [Fact]
    public void RejectsPrematureReplayAndSplitCheckpoint()
    {
        var queue = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var direct = Guid.NewGuid();
        var good = new LibraryBatchQueueObservation("Pending", "StagedInBatch", "StagedInBatch", "NotRequested", 1, 1, 0, 2, queue, [.. queue],
            true, true, "library-batch-closed", [direct], direct, 1, false);
        LibraryBatchQueueAssertions.Verify(good);
        Reject(good with { InitialSaveStatus = "Saved" });
        Reject(good with { SavedInBatchStatus = "Pending" });
        Reject(good with { RestoredStatus = "Saved" });
        Reject(good with { RevertedStatus = "StagedInBatch" });
        Reject(good with { HostAttemptsBeforeMode = 0 });
        Reject(good with { HostAttemptsInMode = 2 });
        Reject(good with { HostWritesInMode = 1 });
        Reject(good with { ReviewedChanges = 1 });
        Reject(good with { ReviewedOrder = [queue[0]] });
        Reject(good with { RestoredOrder = [.. queue.Reverse()] });
        Reject(good with { CheckpointAndEditDurable = false });
        Reject(good with { OrdinaryPendingPreserved = false });
        Reject(good with { DiscardedReplayCode = "published" });
        Reject(good with { AfterDisableOperations = [direct, Guid.NewGuid()] });
        Reject(good with { HostWritesAfterDisable = 2 });
        Reject(good with { FailedCheckpointAddedBatchEntry = true });
    }

    private static void Reject(LibraryBatchQueueObservation result) =>
        Assert.ThrowsAny<XunitException>(() => LibraryBatchQueueAssertions.Verify(result));

    /// <summary>Rejects stale, unverified, or repeated published queue outcomes.</summary>
    /// <remarks>2026-11-24: Establish the mock gate for TEST-LIBRARY-004 and FR-PLAYER-001#ac-queue-autosave.</remarks>
    [Fact]
    public void RejectsPrematureOrStalePublishedQueueBinding()
    {
        var good = new BatchQueueCommitAssertions.Evidence("StagedInBatch", "StagedInBatch", "Saved", "Saved", 9, 9, true, true, true,
            true, true, true, 9, "Saved", 10, 1);
        BatchQueueCommitAssertions.Verify(good);
        BatchQueueCommitAssertions.Evidence[] wrong = [good with { BeforeCommit = "Saved" }, good with { WhileSealed = "Saved" },
            good with { AfterCommit = "NotRequested" }, good with { AfterRestart = "NotRequested" }, good with { LocalVersion = 7 },
            good with { RestartVersion = 7 }, good with { LocalStageCleared = false }, good with { QueueOrderRetained = false },
            good with { DuplicateEntriesRetained = false }, good with { ReplayReusedReceipt = false }, good with { ChangedReplayRejected = false },
            good with { InvalidReceiptRetainedSeal = false }, good with { DirectExpectedVersion = 7 }, good with { DirectStatus = "Conflict" },
            good with { DirectVersion = 9 }, good with { DirectAttempts = 2 }];
        foreach (var result in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchQueueCommitAssertions.Verify(result));
        BatchQueueCommitAssertions.VerifyNewerQueue("NotRequested", 8, true, true);
        Assert.ThrowsAny<XunitException>(() => BatchQueueCommitAssertions.VerifyNewerQueue("Saved", 8, true, true));
        Assert.ThrowsAny<XunitException>(() => BatchQueueCommitAssertions.VerifyNewerQueue("NotRequested", 7, true, true));
        Assert.ThrowsAny<XunitException>(() => BatchQueueCommitAssertions.VerifyNewerQueue("NotRequested", 8, false, true));
        Assert.ThrowsAny<XunitException>(() => BatchQueueCommitAssertions.VerifyNewerQueue("NotRequested", 8, true, false));
    }
}
