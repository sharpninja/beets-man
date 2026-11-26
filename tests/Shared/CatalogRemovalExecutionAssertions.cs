using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Defines independently pinned catalog-removal and restart observations.</summary>
/// <remarks>2026-11-24: Cover TEST-BEETS-002 for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal static class CatalogRemovalExecutionAssertions
{
    internal const string First = "Inbox/Take [1],.flac";
    internal const string Second = "Inbox/Take [2].flac";
    internal const string Other = "Inbox/Keep.flac";

    internal sealed record Evidence(Guid Job, Guid RecoveredJob, Guid Operation, Guid PublishedOperation, string InterruptedPhase,
        string[] NativeAfterInterruption, long InterruptedFeed, int InterruptedDeliveries, string RecoveredPhase, long PublishedFeed, int Publications,
        string[] Tombstones, bool ScopedTombstones, string[] NativeAfterRecovery, string[] AppliedPaths, bool AllAudioPreserved,
        bool ExactHostTrackRemoval, Guid Peer, Guid[] QueuedPeers, string DivergedPhase, string? DivergedCode, bool DivergedRetryable);

    /// <summary>Checks that partial native removal stays unpublished and that restart reconciles and publishes it once.</summary>
    internal static void Verify(Evidence effect)
    {
        Assert.NotEqual(Guid.Empty, effect.Job);
        Assert.Equal(effect.Job, effect.RecoveredJob);
        Assert.NotEqual(Guid.Empty, effect.Operation);
        Assert.Equal(effect.Operation, effect.PublishedOperation);
        Assert.Equal("Running", effect.InterruptedPhase);
        Assert.Equal([Other, Second], effect.NativeAfterInterruption);
        Assert.Equal(42, effect.InterruptedFeed);
        Assert.Equal(0, effect.InterruptedDeliveries);
        Assert.Equal("Published", effect.RecoveredPhase);
        Assert.Equal(43, effect.PublishedFeed);
        Assert.Equal(1, effect.Publications);
        Assert.Equal(["Track:Delete:" + First, "Track:Delete:" + Second], effect.Tombstones);
        Assert.True(effect.ScopedTombstones);
        Assert.Equal([Other], effect.NativeAfterRecovery);
        Assert.Equal([First, Second], effect.AppliedPaths);
        Assert.True(effect.AllAudioPreserved);
        Assert.True(effect.ExactHostTrackRemoval);
        Assert.Equal([effect.Peer], effect.QueuedPeers);
        Assert.Equal("Failed", effect.DivergedPhase);
        Assert.Equal("maintenance-native-track-changed", effect.DivergedCode);
        Assert.False(effect.DivergedRetryable);
    }
}
