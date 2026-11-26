using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Host.Tests;

/// <summary>Proves that the import-defaults assertions detect a separate or stale settings editor.</summary>
/// <remarks>2026-11-25: Verify independent SCR29 command expectations for TEST-BEETS-001.</remarks>
/// <remarks>2026-11-26: Prove permission and revision checks for host import defaults under FR-BEETS-001#ac-open.</remarks>
public sealed class ImportDefaultsEntryMockTests
{
    [Fact]
    public void ImportDefaultsEntryAssertionsRejectWrongEditorAndScope()
    {
        var correct = new ImportDefaultsEntryAssertions.Observation(true, "review-suggested-matches", 0, "keep-tags", 1, true, true,
            "beets-library-host-unavailable");
        ImportDefaultsEntryAssertions.Verify(correct);
        ImportDefaultsEntryAssertions.Observation[] wrong =
        [
            correct with { SharedEditor = false },
            correct with { InitialBehavior = "keep-tags" },
            correct with { InitialRevision = 1 },
            correct with { SavedBehavior = "review-suggested-matches" },
            correct with { SavedRevision = 0 },
            correct with { LocalCategoryPreserved = false },
            correct with { ListenerCannotSave = false },
            correct with { OfflineFailure = "unknown-preference-category" }
        ];
        Assert.All(wrong, observation => Assert.ThrowsAny<XunitException>(() => ImportDefaultsEntryAssertions.Verify(observation)));
    }
}
