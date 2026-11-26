using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Captures persisted review, local isolation, revert, and commit retry behavior.</summary>
/// <remarks>2026-11-24: Trace FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit, ac-batch-revert, and ac-batch-toggle-off.</remarks>
internal sealed record LibraryBatchLifecycleObservation(bool InitiallyEnabled, bool EnabledAfterRestart, string[] ReviewedChanges,
    bool DuplicateStageKeptRevision, string DirtyDisableCode, string StaleApprovalCode, int TransfersBeforeApproval,
    int TransfersAfterRevert, bool RevertKeptMode, int RemainingChangesAfterRevert, string StaleStageCode, string UncertainCommitCode,
    string RevertDuringCommitCode, string DifferentCommitCode, Guid[] TransferOperations, Guid CommitOperation, bool SameSealedRevision,
    Guid PublishedBatchId, Guid ReviewedBatchId, long? PublishedRevision, bool EnabledAfterCommit, int RemainingChangesAfterCommit);

/// <summary>Describes the durable state after the publisher returns an invalid receipt.</summary>
/// <remarks>2026-11-26: Verify that a malformed acknowledgment preserves FR-LIBRARY-002#ac-batch-commit approval and edits.</remarks>
internal sealed record BatchReceiptRejection(string Code, bool Retryable, bool SealRetained, Guid? ReviewId, long Revision, Guid[] EntryOperations);

/// <summary>Describes publication and repeat submission after replacing the malformed receipt.</summary>
/// <remarks>2026-11-26: Verify idempotent finalization for FR-LIBRARY-002#ac-batch-commit.</remarks>
internal sealed record BatchReceiptRecovery(bool SealRetained, int RemainingEntries, int PublisherCalls, Guid[] PublisherOperations,
    Guid PublishedOperation, bool RepeatReturnedSameReceipt);

/// <summary>Checks the operation receipts and guards at the desktop batch boundary.</summary>
internal static class LibraryBatchLifecycleAssertions
{
    /// <summary>Checks that no part of an approved review was cleared or replaced by an invalid acknowledgment.</summary>
    /// <remarks>2026-11-26: Bind the retryable receipt-mismatch contract for FR-LIBRARY-002#ac-batch-commit.</remarks>
    internal static void VerifyRejectedReceipt(BatchReceiptRejection observed, Guid review, long revision, Guid[] entries)
    {
        Assert.Equal("library-batch-receipt-mismatch", observed.Code);
        Assert.True(observed.Retryable);
        Assert.True(observed.SealRetained);
        Assert.Equal(review, observed.ReviewId);
        Assert.Equal(revision, observed.Revision);
        Assert.Equal(entries, observed.EntryOperations);
    }

    /// <summary>Checks that the valid receipt closes the seal once and the original command replays locally.</summary>
    /// <remarks>2026-11-26: Bind one-time settlement and later replay for FR-LIBRARY-002#ac-batch-commit.</remarks>
    internal static void VerifyReceiptRecovery(BatchReceiptRecovery observed, Guid operation, int expectedAttempts)
    {
        Assert.False(observed.SealRetained);
        Assert.Equal(0, observed.RemainingEntries);
        Assert.Equal(expectedAttempts, observed.PublisherCalls);
        Assert.Equal(Enumerable.Repeat(operation, expectedAttempts), observed.PublisherOperations);
        Assert.Equal(operation, observed.PublishedOperation);
        Assert.True(observed.RepeatReturnedSameReceipt);
    }

    internal static void Verify(LibraryBatchLifecycleObservation result)
    {
        Assert.False(result.InitiallyEnabled);
        Assert.True(result.EnabledAfterRestart);
        Assert.Equal(["Track tags", "Playlist save", "Queue auto-save"], result.ReviewedChanges);
        Assert.True(result.DuplicateStageKeptRevision);
        Assert.Equal("library-batch-resolution-required", result.DirtyDisableCode);
        Assert.Equal("stale-library-batch-review", result.StaleApprovalCode);
        Assert.Equal(0, result.TransfersBeforeApproval);
        Assert.Equal(0, result.TransfersAfterRevert);
        Assert.True(result.RevertKeptMode);
        Assert.Equal(0, result.RemainingChangesAfterRevert);
        Assert.Equal("library-batch-closed", result.StaleStageCode);
        Assert.Equal("library-batch-host-unreachable", result.UncertainCommitCode);
        Assert.Equal("library-batch-commit-in-progress", result.RevertDuringCommitCode);
        Assert.Equal("library-batch-commit-in-progress", result.DifferentCommitCode);
        Assert.Equal([result.CommitOperation, result.CommitOperation], result.TransferOperations);
        Assert.True(result.SameSealedRevision);
        Assert.NotEqual(Guid.Empty, result.PublishedBatchId);
        Assert.Equal(result.ReviewedBatchId, result.PublishedBatchId);
        Assert.Equal(43, result.PublishedRevision);
        Assert.False(result.EnabledAfterCommit);
        Assert.Equal(0, result.RemainingChangesAfterCommit);
    }
}
