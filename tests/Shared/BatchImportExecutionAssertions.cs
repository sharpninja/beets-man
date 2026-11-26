using Xunit;

/// <summary>Checks guided import recovery and whole-batch publication independently of the host.</summary>
/// <remarks>2026-11-24: Exercise TEST-LIBRARY-004 and TEST-BEETS-002 across the approval and recovery boundary.</remarks>
/// <remarks>2026-11-26: Share the approved native batch recovery assertions for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal static class BatchImportExecutionAssertions
{
    internal sealed record Evidence(int WritesBeforeFinalChoice, long RevisionAfterInterruptedApply,
        int NativeKeepCalls, int NativeSkipCalls, long PublishedRevision, int PublishedTracks, int PublishedPlaylists,
        int DirectJobs, long RevisionAfterSkipOnly);

    internal static void Verify(Evidence actual)
    {
        Assert.Equal(0, actual.WritesBeforeFinalChoice);
        Assert.Equal(0, actual.RevisionAfterInterruptedApply);
        Assert.Equal(1, actual.NativeKeepCalls);
        Assert.Equal(0, actual.NativeSkipCalls);
        Assert.Equal(1, actual.PublishedRevision);
        Assert.Equal(1, actual.PublishedTracks);
        Assert.Equal(1, actual.PublishedPlaylists);
        Assert.Equal(0, actual.DirectJobs);
        Assert.Equal(actual.PublishedRevision, actual.RevisionAfterSkipOnly);
    }
}
