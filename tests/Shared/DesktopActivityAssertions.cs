using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies observable registered desktop activity independently of authorization revisions.</summary>
/// <remarks>2026-11-25: Pin the SCR42 Last seen field to authenticated activity for FR-RECORDS-001#ac-register.</remarks>
internal static class DesktopActivityAssertions
{
    internal static readonly DateTimeOffset EnrolledAt = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset ObservedAt = EnrolledAt.AddMinutes(1);

    internal sealed record Observation(DateTimeOffset? Legacy, DateTimeOffset? Enrollment, DateTimeOffset? Coalesced,
        DateTimeOffset? Activity, DateTimeOffset? RegressedClock, DateTimeOffset? RejectedScope, long ApprovalRevision,
        long RevisionAfterActivity, long RevokedRevision, DateTimeOffset? AfterRevocation, DateTimeOffset? AfterRestart);

    internal static void Verify(Observation actual)
    {
        Assert.Null(actual.Legacy);
        Assert.Equal(EnrolledAt, actual.Enrollment);
        Assert.Equal(EnrolledAt, actual.Coalesced);
        Assert.Equal(ObservedAt, actual.Activity);
        Assert.Equal(ObservedAt, actual.RegressedClock);
        Assert.Equal(ObservedAt, actual.RejectedScope);
        Assert.Equal(1, actual.ApprovalRevision);
        Assert.Equal(1, actual.RevisionAfterActivity);
        Assert.Equal(2, actual.RevokedRevision);
        Assert.Equal(ObservedAt, actual.AfterRevocation);
        Assert.Equal(ObservedAt, actual.AfterRestart);
    }
}
