using Xunit;
using Xunit.Sdk;
using static BeetsMan.Host.Tests.DesktopActivityAssertions;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises deliberately incorrect desktop activity snapshots before implementation.</summary>
/// <remarks>2026-11-25: Validate activity scoping, time, and unchanged approval revisions for TEST-RECORDS-001.</remarks>
/// <remarks>2026-11-26: Check scoped activity updates preserve the registry revision for FR-RECORDS-001#ac-register.</remarks>
public sealed class DesktopActivityMockTests
{
    [Fact]
    public void ActivityAssertionsRejectWrongTimestampsAndRevisionChanges()
    {
        var correct = new Observation(null, EnrolledAt, EnrolledAt, ObservedAt, ObservedAt, ObservedAt, 1, 1, 2, ObservedAt, ObservedAt);
        Verify(correct);
        Observation[] wrong =
        [
            correct with { Legacy = EnrolledAt },
            correct with { Enrollment = null },
            correct with { Coalesced = ObservedAt },
            correct with { Activity = EnrolledAt },
            correct with { RegressedClock = EnrolledAt },
            correct with { RejectedScope = ObservedAt.AddMinutes(1) },
            correct with { ApprovalRevision = 2 },
            correct with { RevisionAfterActivity = 2 },
            correct with { RevokedRevision = 1 },
            correct with { AfterRevocation = ObservedAt.AddMinutes(1) },
            correct with { AfterRestart = null }
        ];
        Assert.All(wrong, value => Assert.ThrowsAny<XunitException>(() => Verify(value)));
    }
}
