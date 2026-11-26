using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves the non-UI desktop lifecycle assertions detect incorrect ordering and state.</summary>
/// <remarks>2026-11-25: Prove the independent expectations for FR-CONFIG-001#ac-repair and FR-PLAYER-001#ac-resume-playback-after-restart before composing the runtime.</remarks>
public sealed class DesktopStartupMockTests
{
    /// <summary>Proves that the setup assertions detect an early write, premature cursor release, or duplicate activation.</summary>
    /// <remarks>2026-11-25: Use independent observations for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
    [Fact]
    public void SetupPresentationAssertionsRejectWrongOutcomes()
    {
        var correct = new DesktopSetupPresentationAssertions.Evidence(true, true, true, true, true, true, true, true);
        DesktopSetupPresentationAssertions.Verify(correct);
        DesktopSetupPresentationAssertions.Evidence[] wrong =
        [
            correct with { FirstRunWaitedForChoice = false },
            correct with { DatabaseApprovalMatchedPath = false },
            correct with { WaitedDuringWorkspaceActivation = false },
            correct with { OpenedRequestedPaths = false },
            correct with { CursorReleased = false },
            correct with { RetryReusedWorkspace = false },
            correct with { ExistingSettingsRequireRestart = false },
            correct with { SetupOwnersDisposed = false }
        ];
        Assert.All(wrong, result => Assert.ThrowsAny<XunitException>(() => DesktopSetupPresentationAssertions.Verify(result)));
    }

    /// <summary>Proves the library composition assertions reject broken ownership, order, and batch feedback.</summary>
    /// <remarks>2026-11-25: Run independent controls for FR-LIBRARY-001#ac-browse and FR-LIBRARY-002#ac-batch-review.</remarks>
    [Fact]
    public void LibraryWorkspaceAssertionsRejectWrongOutcomes()
    {
        var correct = new LibraryWorkspacePresentationAssertions.Evidence(true, true, true, true, true, true, true, true, true);
        LibraryWorkspacePresentationAssertions.Verify(correct);
        LibraryWorkspacePresentationAssertions.Evidence[] wrong =
        [
            correct with { OpenedOfflineCatalog = false },
            correct with { BoundSharedQueue = false },
            correct with { CopiedInDisplayedOrder = false },
            correct with { StartedWithLocalBatchMode = false },
            correct with { StageEnabledReview = false },
            correct with { ReviewListedExactDraft = false },
            correct with { StageInvalidatedEarlierApproval = false },
            correct with { RevertClearedStage = false },
            correct with { ClosedAllOwners = false }
        ];
        Assert.All(wrong, result => Assert.ThrowsAny<XunitException>(() => LibraryWorkspacePresentationAssertions.Verify(result)));
    }

    /// <summary>Proves each recovery diagnostic assertion rejects its corresponding incorrect observation.</summary>
    /// <remarks>2026-11-25: Exercise independent fixtures for FR-CONFIG-001#ac-repair and ac-logs.</remarks>
    [Fact]
    public void RecoveryDiagnosticAssertionsRejectWrongOutcomes()
    {
        var correct = new RecoveryDiagnosticsAssertions.Observation(true, true, true, true, true, true, true, true);
        RecoveryDiagnosticsAssertions.Verify(correct);
        RecoveryDiagnosticsAssertions.Observation[] wrong =
        [
            correct with { RepairKeptWorkspaceClosed = false },
            correct with { SourcePreserved = false },
            correct with { HasStructuredWarning = false },
            correct with { CorrelationFilterSelectsAttempt = false },
            correct with { UnrelatedFilterIsEmpty = false },
            correct with { PrivateContentOmitted = false },
            correct with { NormalWorkspaceStartsAfterRepair = false },
            correct with { RecoveryDidNotWriteAFile = false }
        ];
        Assert.All(wrong, result => Assert.ThrowsAny<XunitException>(() => RecoveryDiagnosticsAssertions.Verify(result)));
    }

    [Fact]
    public void LifecycleAssertionsRejectWrongOutcomes()
    {
        var fixture = new DesktopStartupAssertions.Observation(true, true, true, true, true, true, 8_000_000, true, true, true);
        DesktopStartupAssertions.Verify(fixture);
        DesktopStartupAssertions.Observation[] wrong =
        [
            fixture with { RepairKeptWorkspaceClosed = false },
            fixture with { CreatedDefaultFiles = false },
            fixture with { CachedCatalogAvailable = false },
            fixture with { RepeatedStartReusedSession = false },
            fixture with { ReopenedSameSession = false },
            fixture with { ResumedSavedOccurrence = false },
            fixture with { ResumeTicks = 0 },
            fixture with { FailedStopKeptResourcesOpen = false },
            fixture with { StopSavedBeforeDisposing = false },
            fixture with { ClosedExactlyOnce = false }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => DesktopStartupAssertions.Verify(observation)));
    }
}
