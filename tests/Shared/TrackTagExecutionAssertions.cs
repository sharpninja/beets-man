using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Captures the catalog, native file, and feed effects of a recovered track tag edit.</summary>
/// <remarks>2026-11-24: Verify FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal sealed record TrackTagExecutionObservation(Guid JobId, Guid RecoveredJobId, Guid OperationId, Guid PublishedOperationId,
    string OriginalGenre, string RecoveredGenre, string EmbeddedGenre, string? EmbeddedYear, bool RecoveredAtOriginalPath,
    bool SourceBytesRetained, bool SiblingBytesRetained, bool EncodedAudioRetained, string InterruptedPhase, long InterruptedFeedRevision,
    string RecoveredPhase, long RecoveredFeedRevision, int NativeInvocations, int PublicationCount, Guid Recipient, Guid[] NotifiedPeers);

/// <summary>Checks that verified file and catalog changes publish once after interruption.</summary>
internal static class TrackTagExecutionAssertions
{
    internal const string UpdatedGenre = "Downtempo $title 100%";

    internal static void Verify(TrackTagExecutionObservation result)
    {
        Assert.NotEqual(Guid.Empty, result.JobId);
        Assert.Equal(result.JobId, result.RecoveredJobId);
        Assert.NotEqual(Guid.Empty, result.OperationId);
        Assert.Equal(result.OperationId, result.PublishedOperationId);
        Assert.Equal("Ambient", result.OriginalGenre);
        Assert.Equal(UpdatedGenre, result.RecoveredGenre);
        Assert.Equal(UpdatedGenre, result.EmbeddedGenre);
        Assert.Null(result.EmbeddedYear);
        Assert.True(result.RecoveredAtOriginalPath);
        Assert.True(result.SourceBytesRetained);
        Assert.True(result.SiblingBytesRetained);
        Assert.True(result.EncodedAudioRetained);
        Assert.Equal("Running", result.InterruptedPhase);
        Assert.Equal(42, result.InterruptedFeedRevision);
        Assert.Equal("Published", result.RecoveredPhase);
        Assert.Equal(43, result.RecoveredFeedRevision);
        Assert.Equal(1, result.NativeInvocations);
        Assert.Equal(1, result.PublicationCount);
        Assert.Equal([result.Recipient], result.NotifiedPeers);
    }
}
