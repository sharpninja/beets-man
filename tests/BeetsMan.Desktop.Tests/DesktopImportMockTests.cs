using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves desktop guided import assertions before binding file and host adapters.</summary>
/// <remarks>2026-11-24: Mock gate for FR-BEETS-001#ac-import, ac-recover, and FR-DISPATCH-001#ac-responsive.</remarks>
public sealed class DesktopImportMockTests
{
    /// <summary>Rejects early submission, changed intent, and loss of the original selection or receipt.</summary>
    [Fact]
    public void DesktopImportAssertionsRejectForeignOrUnrecoverableSelections()
    {
        var job = new Guid("c20e9cd0-b8ab-4bb2-9dae-227fa41f1235");
        var scripted = new Queue<string>(["manifest", "part:0", "part:1", "start", "start", "job"]);
        var actual = new DesktopImportObservation(["Selected A.flac", "Selected B.flac"], [.. scripted], true, true,
            true, true, true, true, true, job, job, "Accepted", true);
        DesktopImportAssertions.Verify(actual);
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { SelectedNames = ["Changed.flac"] }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { Calls = ["manifest", "start", "part:0", "part:1", "job"] }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { CancelledSelection = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { RejectedForeignSelection = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { RetainedSelectionOnFailure = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { ReleasedSelectionAfterAcceptance = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { UsedSameUploadAndOperation = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { ChangedSourceRejected = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { SourceBytesIntact = false }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { JobAfterRetry = Guid.Empty }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { InspectedJob = Guid.NewGuid() }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { InspectedPhase = "Published" }));
        Assert.ThrowsAny<XunitException>(() => DesktopImportAssertions.Verify(actual with { StayedInteractive = false }));
    }
}
