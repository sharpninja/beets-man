using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises guiding and recovering imports with independently scripted beets effects.</summary>
/// <remarks>2026-11-24: Establish the mock assertions for FR-BEETS-001#ac-import and ac-recover before implementing the worker.</remarks>
public sealed class ImportWorkerMockTests
{
    /// <summary>Detects external writes before the journal or publication before complete readback.</summary>
    [Fact]
    public void RequiresJournalAndConfirmedReadbackForFirstRun()
    {
        var observed = new ImportWorkerObservation(["read", "write", "read"], true, 1, "Published", null, false, 42, true);
        ImportWorkerAssertions.FirstRun(observed);
        Reject(ImportWorkerAssertions.FirstRun, observed with { Calls = ["write", "read"] });
        Reject(ImportWorkerAssertions.FirstRun, observed with { Calls = ["read", "write"] });
        Reject(ImportWorkerAssertions.FirstRun, observed with { AttemptWasRecordedBeforeProcess = false });
        Reject(ImportWorkerAssertions.FirstRun, observed with { WriteCalls = 2 });
        Reject(ImportWorkerAssertions.FirstRun, observed with { CatalogRevision = 41 });
        Reject(ImportWorkerAssertions.FirstRun, observed with { OriginalCopiesPreserved = false });
    }

    /// <summary>Detects fresh writes after restart for both known-complete and incomplete beets effects.</summary>
    [Fact]
    public void ReconciliationControlsRecoveryAfterRestart()
    {
        var recovered = new ImportWorkerObservation(["read"], true, 0, "Published", null, false, 42, true);
        var uncertain = new ImportWorkerObservation(["read"], true, 0, "Failed", "import-reconciliation-incomplete", true, null, true);
        ImportWorkerAssertions.Recovered(recovered);
        ImportWorkerAssertions.Uncertain(uncertain);
        Reject(ImportWorkerAssertions.Recovered, recovered with { WriteCalls = 1 });
        Reject(ImportWorkerAssertions.Recovered, recovered with { Retryable = true });
        Reject(ImportWorkerAssertions.Uncertain, uncertain with { Calls = ["read", "write", "read"] });
        Reject(ImportWorkerAssertions.Uncertain, uncertain with { Phase = "Published" });
        Reject(ImportWorkerAssertions.Uncertain, uncertain with { CatalogRevision = 42 });
        Reject(ImportWorkerAssertions.Uncertain, uncertain with { Retryable = false });
    }

    private static void Reject(Action<ImportWorkerObservation> verify, ImportWorkerObservation observed) =>
        Assert.ThrowsAny<XunitException>(() => verify(observed));
}
