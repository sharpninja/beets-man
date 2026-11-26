using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks approved audio removal from preview through restart and peer delivery.</summary>
/// <remarks>2026-11-24: Bind TEST-BEETS-002 for FR-BEETS-001#ac-edit and ac-recover.</remarks>
internal static class AudioRemovalExecutionAssertions
{
    internal sealed record Evidence(bool PreviewRetainedBytes, bool MissingApprovalDenied, bool PrivateCheckpoint, bool RestartDeletedOnlyReviewed,
        int NativeDeletes, int FileDeletes, int Publications, long Revision, bool PeerAcknowledged, bool ExactReplay, bool ReplacementRejected, bool ReplacementRetained);

    /// <summary>Checks that each destructive effect stays within the separately approved selection.</summary>
    internal static void Verify(Evidence evidence)
    {
        Assert.True(evidence.PreviewRetainedBytes);
        Assert.True(evidence.MissingApprovalDenied);
        Assert.True(evidence.PrivateCheckpoint);
        Assert.True(evidence.RestartDeletedOnlyReviewed);
        Assert.Equal(2, evidence.NativeDeletes);
        Assert.Equal(2, evidence.FileDeletes);
        Assert.Equal(1, evidence.Publications);
        Assert.Equal(43, evidence.Revision);
        Assert.True(evidence.PeerAcknowledged);
        Assert.True(evidence.ExactReplay);
        Assert.True(evidence.ReplacementRejected);
        Assert.True(evidence.ReplacementRetained);
    }
}
