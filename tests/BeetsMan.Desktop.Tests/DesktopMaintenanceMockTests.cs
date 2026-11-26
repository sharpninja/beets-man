using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the desktop maintenance acceptance assertions reject independently scripted bad outcomes.</summary>
/// <remarks>2026-11-24: Validate the mock fixtures for FR-BEETS-001#ac-edit, ac-recover and FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-revert before binding the real routes.</remarks>
public sealed class DesktopMaintenanceMockTests
{
    /// <summary>Exercises the same assertions used for typed commands, authenticated host traffic, and LiteDB.</summary>
    [Fact]
    public void RejectsChangedConfirmationAndNonlocalMaintenanceEffects()
    {
        var operation = Guid.Parse("7b837272-511a-48bc-a7b2-24a3de2086b1");
        var job = Guid.Parse("9fabadc2-198c-4be9-a350-4e3751d12531");
        var evidence = new DesktopMaintenanceAssertions.Evidence(operation, operation, job, true, 2, 1, true, true,
            ["Inbox/Waiting.flac -> Lena Vale/Northern Lines/01 Harbor Lights.flac", "CatalogOnly:", "CatalogAndAudio:Inbox/Other.flac"],
            true, true, true, "operation-intent-mismatch", 0, "library-batch-closed", true);
        DesktopMaintenanceAssertions.Verify(evidence);
        DesktopMaintenanceAssertions.Evidence[] rejected =
        [
            evidence with { FirstConfirmation = Guid.Empty, RetriedConfirmation = Guid.Empty },
            evidence with { RetriedConfirmation = job },
            evidence with { AcceptedJob = Guid.Empty },
            evidence with { RetainedPreviewAfterLostReply = false },
            evidence with { RemoteConfirmations = 3 },
            evidence with { HostJobs = 2 },
            evidence with { WrongApprovalRejected = false },
            evidence with { CanConfirmOffline = false },
            evidence with { ReviewedEffects = ["Inbox/Waiting.flac -> outside.flac", "CatalogOnly:", "CatalogAndAudio:Inbox/Other.flac"] },
            evidence with { ReviewedEffects = [evidence.ReviewedEffects[0], "CatalogOnly:Inbox/Waiting.flac", evidence.ReviewedEffects[2]] },
            evidence with { ReviewedEffects = [evidence.ReviewedEffects[0], evidence.ReviewedEffects[1], "CatalogAndAudio:"] },
            evidence with { OrderedTrackSelectionRetained = false },
            evidence with { LocalReceiptsOnly = false },
            evidence with { ReplayedAfterRestart = false },
            evidence with { ChangedReplay = "accepted" },
            evidence with { RemainingChanges = 1 },
            evidence with { LateReplay = "accepted" },
            evidence with { CatalogAndMediaUnchanged = false }
        ];
        Assert.All(rejected, item => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => DesktopMaintenanceAssertions.Verify(item)));
    }
}
