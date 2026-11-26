using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Captures the observable order and result of applying or recovering one guided album.</summary>
/// <remarks>2026-11-24: Exercise journal-first execution and reconciliation for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record ImportWorkerObservation(string[] Calls, bool AttemptWasRecordedBeforeProcess, int WriteCalls,
    string Phase, string? FailureCode, bool Retryable, long? CatalogRevision, bool OriginalCopiesPreserved);

/// <summary>Checks both the first accepted attempt and recovery without a duplicate external write.</summary>
internal static class ImportWorkerAssertions
{
    /// <summary>Requires lookup, one journaled write, a confirmed readback and its publication.</summary>
    public static void FirstRun(ImportWorkerObservation observed)
    {
        Assert.Equal(["read", "write", "read"], observed.Calls);
        Assert.True(observed.AttemptWasRecordedBeforeProcess);
        Assert.Equal(1, observed.WriteCalls);
        Published(observed);
    }

    /// <summary>Requires the restarted worker to publish already completed native effects without another write.</summary>
    public static void Recovered(ImportWorkerObservation observed)
    {
        Assert.Equal(["read"], observed.Calls);
        Assert.Equal(0, observed.WriteCalls);
        Published(observed);
    }

    /// <summary>Requires the restarted worker to hold uncertain or incomplete effects for recovery.</summary>
    public static void Uncertain(ImportWorkerObservation observed)
    {
        Assert.Equal(["read"], observed.Calls);
        Assert.Equal(0, observed.WriteCalls);
        Assert.Equal("Failed", observed.Phase);
        Assert.Equal("import-reconciliation-incomplete", observed.FailureCode);
        Assert.True(observed.Retryable);
        Assert.Null(observed.CatalogRevision);
        Assert.True(observed.OriginalCopiesPreserved);
    }

    private static void Published(ImportWorkerObservation observed)
    {
        Assert.Equal("Published", observed.Phase);
        Assert.Null(observed.FailureCode);
        Assert.False(observed.Retryable);
        Assert.Equal(42, observed.CatalogRevision);
        Assert.True(observed.OriginalCopiesPreserved);
    }
}
