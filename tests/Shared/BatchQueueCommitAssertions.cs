using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the queue's published revision and its next edit at the batch boundary.</summary>
/// <remarks>2026-11-24: Cover TEST-LIBRARY-004 and TEST-PLAYER-001 for FR-LIBRARY-002#ac-batch-commit and FR-PLAYER-001#ac-queue-autosave.</remarks>
internal static class BatchQueueCommitAssertions
{
    internal sealed record Evidence(string BeforeCommit, string WhileSealed, string AfterCommit, string AfterRestart,
        long LocalVersion, long RestartVersion, bool LocalStageCleared, bool QueueOrderRetained, bool DuplicateEntriesRetained,
        bool ReplayReusedReceipt, bool ChangedReplayRejected, bool InvalidReceiptRetainedSeal, long DirectExpectedVersion, string DirectStatus,
        long DirectVersion, int DirectAttempts);

    /// <summary>Verifies local staging, sealed recovery, authoritative rebinding and a subsequent direct edit.</summary>
    internal static void Verify(Evidence observed)
    {
        Assert.Equal("StagedInBatch", observed.BeforeCommit);
        Assert.Equal("StagedInBatch", observed.WhileSealed);
        Assert.Equal("Saved", observed.AfterCommit);
        Assert.Equal("Saved", observed.AfterRestart);
        Assert.Equal(9, observed.LocalVersion);
        Assert.Equal(9, observed.RestartVersion);
        Assert.True(observed.LocalStageCleared);
        Assert.True(observed.QueueOrderRetained);
        Assert.True(observed.DuplicateEntriesRetained);
        Assert.True(observed.ReplayReusedReceipt);
        Assert.True(observed.ChangedReplayRejected);
        Assert.True(observed.InvalidReceiptRetainedSeal);
        Assert.Equal(9, observed.DirectExpectedVersion);
        Assert.Equal("Saved", observed.DirectStatus);
        Assert.Equal(10, observed.DirectVersion);
        Assert.Equal(1, observed.DirectAttempts);
    }

    /// <summary>Checks that a newer playback-only edit stays private while its playlist base advances.</summary>
    internal static void VerifyNewerQueue(string status, long playlistRevision, bool playbackKeptLatest, bool hostKeptReviewed)
    {
        Assert.Equal("NotRequested", status);
        Assert.Equal(8, playlistRevision);
        Assert.True(playbackKeptLatest);
        Assert.True(hostKeptReviewed);
    }
}
