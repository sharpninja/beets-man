using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises the independent expectations for explicitly approved audio removal.</summary>
/// <remarks>2026-11-24: Establish the TEST-BEETS-002 mock gate before native execution changes.</remarks>
/// <remarks>2026-11-26: Verify native removal approval and recovery after a partial removal for FR-BEETS-001#ac-edit and FR-BEETS-001#ac-recover.</remarks>
public sealed class AudioRemovalExecutionMockTests
{
    [Fact]
    public void RejectsUnexpectedFileRemovalAndPrematurePublication()
    {
        var expected = new AudioRemovalExecutionAssertions.Evidence(true, true, true, true, 2, 2, 1, 43, true, true, true, true);
        AudioRemovalExecutionAssertions.Verify(expected);
        AudioRemovalExecutionAssertions.Evidence[] wrong = [expected with { PreviewRetainedBytes = false }, expected with { MissingApprovalDenied = false },
            expected with { PrivateCheckpoint = false }, expected with { RestartDeletedOnlyReviewed = false }, expected with { NativeDeletes = 3 },
            expected with { FileDeletes = 3 }, expected with { Publications = 2 }, expected with { Revision = 44 }, expected with { PeerAcknowledged = false },
            expected with { ExactReplay = false }, expected with { ReplacementRejected = false }, expected with { ReplacementRetained = false }];
        foreach (var actual in wrong)
            Assert.ThrowsAny<XunitException>(() => AudioRemovalExecutionAssertions.Verify(actual));
    }
}
