using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the import execution assertions discriminate premature, duplicate and missing effects.</summary>
/// <remarks>2026-11-24: Establish the TEST-LIBRARY-004 and TEST-BEETS-002 mock gate before implementation.</remarks>
/// <remarks>2026-11-26: Prove recovery before publication for FR-BEETS-001#ac-recover and FR-LIBRARY-002#ac-batch-commit.</remarks>
public sealed class BatchImportExecutionMockTests
{
    [Fact]
    public void RefusesEarlyAndDuplicateNativeWritesAndNonAtomicPublication()
    {
        var expected = new BatchImportExecutionAssertions.Evidence(0, 0, 1, 0, 1, 1, 1, 0, 1);
        BatchImportExecutionAssertions.Verify(expected);
        BatchImportExecutionAssertions.Evidence[] wrong =
        [
            expected with { WritesBeforeFinalChoice = 1 }, expected with { RevisionAfterInterruptedApply = 1 },
            expected with { NativeKeepCalls = 2 }, expected with { NativeSkipCalls = 1 }, expected with { PublishedRevision = 2 },
            expected with { PublishedTracks = 0 }, expected with { PublishedPlaylists = 0 }, expected with { DirectJobs = 1 },
            expected with { RevisionAfterSkipOnly = 2 }
        ];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => BatchImportExecutionAssertions.Verify(actual));
    }
}
