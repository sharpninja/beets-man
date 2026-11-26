using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies that Settings uses the authorized host editor and its current revision.</summary>
/// <remarks>2026-11-25: Pin the SCR29 entry and STB08 host configuration behavior for FR-BEETS-001#ac-open.</remarks>
internal static class ImportDefaultsEntryAssertions
{
    internal sealed record Observation(bool SharedEditor, string InitialBehavior, long InitialRevision, string SavedBehavior,
        long SavedRevision, bool LocalCategoryPreserved, bool ListenerCannotSave, string OfflineFailure);

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.SharedEditor);
        Assert.Equal("review-suggested-matches", actual.InitialBehavior);
        Assert.Equal(0, actual.InitialRevision);
        Assert.Equal("keep-tags", actual.SavedBehavior);
        Assert.Equal(1, actual.SavedRevision);
        Assert.True(actual.LocalCategoryPreserved);
        Assert.True(actual.ListenerCannotSave);
        Assert.Equal("beets-library-host-unavailable", actual.OfflineFailure);
    }
}
