using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Demonstrates that the diagnostics handoff assertions reject incorrect behavior.</summary>
/// <remarks>2026-11-25: Test the approved SCR03 command before implementing it for TEST-CONFIG-001.</remarks>
/// <remarks>2026-11-26: Reject an incorrect path or failed handoff for FR-CONFIG-001#ac-logs.</remarks>
public sealed class LogFolderMockTests
{
    [Fact]
    public void HandoffAssertionsRejectPrematureSettingsAndFalseReceipts()
    {
        const string active = "default-log-directory";
        const string proposed = "configured-log-directory";
        var correct = new LogFolderAssertions.Observation([new(active, true), new(proposed, true), new(proposed, true)],
            active, proposed, "log-folder-open-failed", true, true);
        LogFolderAssertions.Verify(correct, active, proposed);
        LogFolderAssertions.Observation[] incorrect =
        [
            correct with { Handoffs = [new(proposed, true), new(proposed, true), new(proposed, true)] },
            correct with { Handoffs = [new(active, false), new(proposed, true), new(proposed, true)] },
            correct with { BeforeRestartReceipt = proposed },
            correct with { AfterRestartReceipt = active },
            correct with { Failure = "" },
            correct with { FailedHandoffHasNoReceipt = false },
            correct with { CancelledHandoffWasNotStarted = false }
        ];
        Assert.All(incorrect, result => Assert.ThrowsAny<XunitException>(() => LogFolderAssertions.Verify(result, active, proposed)));
    }
}
