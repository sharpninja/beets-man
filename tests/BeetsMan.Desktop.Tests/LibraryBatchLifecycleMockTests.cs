using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks that local batch tests detect early transmission, missing edits, and recovery failures.</summary>
/// <remarks>2026-11-24: Prove TEST-LIBRARY-003 assertions against independent observable fixtures before coding.</remarks>
public sealed class LibraryBatchLifecycleMockTests
{
    /// <summary>Proves the rejection and recovery assertions distinguish durable batch and receipt failures.</summary>
    /// <remarks>2026-11-26: Exercise independent fixtures and negative controls for FR-LIBRARY-002#ac-batch-commit.</remarks>
    [Fact]
    public void NamedPlaylistReceiptSetMustMatchReviewedPlaylistEntries()
    {
        var review = Guid.Parse("b4ad494a-109a-494a-9248-7f0807cc0578");
        var operation = Guid.Parse("7796ced2-1a11-47b1-b4cf-7d3b5fe0d461");
        Guid[] entries = [Guid.Parse("1233e42b-6a5c-413c-bd08-0903e773bbd7"), Guid.Parse("8873fb0d-10f6-46b2-b548-c6ced9ce24b8")];
        var rejected = new BatchReceiptRejection("library-batch-receipt-mismatch", true, true, review, 7, entries);
        LibraryBatchLifecycleAssertions.VerifyRejectedReceipt(rejected, review, 7, entries);
        RejectReceipt(rejected with { Code = "success" });
        RejectReceipt(rejected with { Retryable = false });
        RejectReceipt(rejected with { SealRetained = false });
        RejectReceipt(rejected with { ReviewId = Guid.Empty });
        RejectReceipt(rejected with { Revision = 8 });
        RejectReceipt(rejected with { EntryOperations = [entries[0]] });

        var recovered = new BatchReceiptRecovery(false, 0, 2, [operation, operation], operation, true);
        LibraryBatchLifecycleAssertions.VerifyReceiptRecovery(recovered, operation, 2);
        RejectRecovery(recovered with { SealRetained = true });
        RejectRecovery(recovered with { RemainingEntries = 1 });
        RejectRecovery(recovered with { PublisherCalls = 3 });
        RejectRecovery(recovered with { PublisherOperations = [operation, Guid.Empty] });
        RejectRecovery(recovered with { PublishedOperation = Guid.Empty });
        RejectRecovery(recovered with { RepeatReturnedSameReceipt = false });

        void RejectReceipt(BatchReceiptRejection bad) =>
            Assert.ThrowsAny<XunitException>(() => LibraryBatchLifecycleAssertions.VerifyRejectedReceipt(bad, review, 7, entries));
        void RejectRecovery(BatchReceiptRecovery bad) =>
            Assert.ThrowsAny<XunitException>(() => LibraryBatchLifecycleAssertions.VerifyReceiptRecovery(bad, operation, 2));
    }

    [Fact]
    public void RejectsBatchIsolationAndApprovalFailures()
    {
        var batch = Guid.Parse("b4ad494a-109a-494a-9248-7f0807cc0578");
        var commit = Guid.Parse("7796ced2-1a11-47b1-b4cf-7d3b5fe0d461");
        var good = new LibraryBatchLifecycleObservation(false, true, ["Track tags", "Playlist save", "Queue auto-save"], true,
            "library-batch-resolution-required", "stale-library-batch-review", 0, 0, true, 0, "library-batch-closed",
            "library-batch-host-unreachable", "library-batch-commit-in-progress", "library-batch-commit-in-progress", [commit, commit], commit,
            true, batch, batch, 43, false, 0);
        LibraryBatchLifecycleAssertions.Verify(good);
        Reject(good with { InitiallyEnabled = true });
        Reject(good with { EnabledAfterRestart = false });
        Reject(good with { ReviewedChanges = ["Track tags", "Playlist save"] });
        Reject(good with { DuplicateStageKeptRevision = false });
        Reject(good with { DirtyDisableCode = "disabled" });
        Reject(good with { StaleApprovalCode = "accepted" });
        Reject(good with { TransfersBeforeApproval = 1 });
        Reject(good with { TransfersAfterRevert = 1 });
        Reject(good with { RevertKeptMode = false });
        Reject(good with { RemainingChangesAfterRevert = 1 });
        Reject(good with { StaleStageCode = "accepted" });
        Reject(good with { UncertainCommitCode = "success" });
        Reject(good with { RevertDuringCommitCode = "reverted" });
        Reject(good with { DifferentCommitCode = "accepted" });
        Reject(good with { TransferOperations = [commit, Guid.NewGuid()] });
        Reject(good with { SameSealedRevision = false });
        Reject(good with { PublishedBatchId = Guid.Empty });
        Reject(good with { PublishedBatchId = Guid.NewGuid() });
        Reject(good with { PublishedRevision = 44 });
        Reject(good with { EnabledAfterCommit = true });
        Reject(good with { RemainingChangesAfterCommit = 1 });
    }

    private static void Reject(LibraryBatchLifecycleObservation bad) =>
        Assert.ThrowsAny<XunitException>(() => LibraryBatchLifecycleAssertions.Verify(bad));
}
