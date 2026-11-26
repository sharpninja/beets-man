using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Defines the diagnostics available when the normal desktop workspace cannot start.</summary>
/// <remarks>2026-11-25: Cover the repair and redaction contract for FR-CONFIG-001#ac-repair, ac-logs and TEST-CONFIG-002.</remarks>
internal static class RecoveryDiagnosticsAssertions
{
    internal sealed record Observation(bool RepairKeptWorkspaceClosed, bool SourcePreserved, bool HasStructuredWarning,
        bool CorrelationFilterSelectsAttempt, bool UnrelatedFilterIsEmpty, bool PrivateContentOmitted, bool NormalWorkspaceStartsAfterRepair,
        bool RecoveryDidNotWriteAFile);

    internal static void Verify(Observation result)
    {
        Assert.True(result.RepairKeptWorkspaceClosed);
        Assert.True(result.SourcePreserved);
        Assert.True(result.HasStructuredWarning);
        Assert.True(result.CorrelationFilterSelectsAttempt);
        Assert.True(result.UnrelatedFilterIsEmpty);
        Assert.True(result.PrivateContentOmitted);
        Assert.True(result.NormalWorkspaceStartsAfterRepair);
        Assert.True(result.RecoveryDidNotWriteAFile);
    }
}
