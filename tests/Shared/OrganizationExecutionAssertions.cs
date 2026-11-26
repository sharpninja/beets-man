using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Checks exact organization effects across approval, interruption and peer publication.</summary>
/// <remarks>2026-11-24: Cover TEST-BEETS-002 for FR-BEETS-001#ac-edit and ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
internal static class OrganizationExecutionAssertions
{
    internal const string Source = "Inbox/Waiting.flac";
    internal const string Destination = "Lena Vale/Northern Lines/01 Harbor Lights.flac";

    internal sealed record Evidence(string PreviewSource, string PreviewDestination, bool PreviewWasReadOnly, string InterruptedPhase,
        string NativePathAtInterruption, bool OriginalsRetained, bool DestinationVerified, bool ConcurrentProjectionUnchanged,
        string FinalPhase, long CatalogRevision, string PeerPath, int NativePathWrites, int PublicationCount, bool SameJobOnReplay,
        bool LateCollisionRejected, bool ForeignBytesUnchanged, bool AlternateDestinationAbsent);

    /// <summary>Verifies the reviewed path, preserved source, recovery, and absence of foreign file effects.</summary>
    internal static void Verify(Evidence observed)
    {
        Assert.Equal(Source, observed.PreviewSource);
        Assert.Equal(Destination, observed.PreviewDestination);
        Assert.True(observed.PreviewWasReadOnly);
        Assert.Equal("Running", observed.InterruptedPhase);
        Assert.Equal(Destination, observed.NativePathAtInterruption);
        Assert.True(observed.OriginalsRetained);
        Assert.True(observed.DestinationVerified);
        Assert.True(observed.ConcurrentProjectionUnchanged);
        Assert.Equal("Published", observed.FinalPhase);
        Assert.Equal(43, observed.CatalogRevision);
        Assert.Equal(Destination, observed.PeerPath);
        Assert.Equal(1, observed.NativePathWrites);
        Assert.Equal(1, observed.PublicationCount);
        Assert.True(observed.SameJobOnReplay);
        Assert.True(observed.LateCollisionRejected);
        Assert.True(observed.ForeignBytesUnchanged);
        Assert.True(observed.AlternateDestinationAbsent);
    }
}
