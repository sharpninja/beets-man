using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks an approved organization entry followed by tag and removal effects on its new paths.</summary>
/// <remarks>2026-11-24: Cover TEST-LIBRARY-004 and TEST-BEETS-002 for FR-LIBRARY-002#ac-batch-commit and FR-BEETS-001#ac-recover.</remarks>
internal static class BatchOrganizationAssertions
{
    internal sealed record Evidence(bool PrivateCheckpoint, int Copies, string TagPath, string RemovalPath, bool RetainedOriginals,
        bool EditedDestinationOnly, bool RemovedFromCatalogOnly, int Publications, long Revision, string[] Changes, bool CorrectPeerProjection,
        bool ExactRetry, bool RejectedCollision, bool CollisionBytesRetained);

    /// <summary>Verifies restart recovery, ordered native paths, and a single whole-batch peer publication.</summary>
    internal static void Verify(Evidence result)
    {
        Assert.True(result.PrivateCheckpoint);
        Assert.Equal(2, result.Copies);
        Assert.Equal(OrganizationExecutionAssertions.Destination, result.TagPath);
        Assert.Equal("Lena Vale/Northern Lines/02 Night Crossing.flac", result.RemovalPath);
        Assert.True(result.RetainedOriginals);
        Assert.True(result.EditedDestinationOnly);
        Assert.True(result.RemovedFromCatalogOnly);
        Assert.Equal(1, result.Publications);
        Assert.Equal(43, result.Revision);
        Assert.Equal(["Track:Upsert", "Track:Delete", "Playlist:Upsert"], result.Changes);
        Assert.True(result.CorrectPeerProjection);
        Assert.True(result.ExactRetry);
        Assert.True(result.RejectedCollision);
        Assert.True(result.CollisionBytesRetained);
    }
}
