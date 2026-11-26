using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks that a mixed batch deletes the originally reviewed audio path after earlier edits.</summary>
/// <remarks>2026-11-24: Cover TEST-LIBRARY-004 and TEST-BEETS-002 for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal static class BatchAudioRemovalAssertions
{
    internal sealed record Evidence(bool HeldProjectionDuringRestart, string[] TagPaths, string CatalogRemovalPath, string[] DeletedPaths,
        bool OrganizedCopyKeptItsApprovedTags, bool OtherOriginalRetained, bool FinalPeerState, int Publications, long Revision,
        bool ReplacedAudioRejectedWithoutWrites, bool CollisionRejectedWithoutWrites, bool StableReplay, int Copies);

    /// <summary>Verifies that catalog retargeting cannot silently widen the exact file deletion approval.</summary>
    internal static void Verify(Evidence result)
    {
        Assert.True(result.HeldProjectionDuringRestart);
        Assert.Equal(["Inbox/Other.flac", OrganizationExecutionAssertions.Destination], result.TagPaths);
        Assert.Equal("Lena Vale/Northern Lines/02 Night Crossing.flac", result.CatalogRemovalPath);
        Assert.Equal(["Inbox/Other.flac"], result.DeletedPaths);
        Assert.True(result.OrganizedCopyKeptItsApprovedTags);
        Assert.True(result.OtherOriginalRetained);
        Assert.True(result.FinalPeerState);
        Assert.Equal(1, result.Publications);
        Assert.Equal(43, result.Revision);
        Assert.True(result.ReplacedAudioRejectedWithoutWrites);
        Assert.True(result.CollisionRejectedWithoutWrites);
        Assert.True(result.StableReplay);
        Assert.Equal(2, result.Copies);
    }
}
