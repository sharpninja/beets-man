using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the observable non-UI desktop lifecycle against independent expectations.</summary>
/// <remarks>2026-11-25: Verify bootstrap isolation and restart handoff for FR-CONFIG-001#ac-defaults, FR-CONFIG-001#ac-repair and FR-PLAYER-001#ac-resume-playback-after-restart.</remarks>
internal static class DesktopStartupAssertions
{
    internal sealed record Observation(bool RepairKeptWorkspaceClosed, bool CreatedDefaultFiles, bool CachedCatalogAvailable,
        bool RepeatedStartReusedSession, bool ReopenedSameSession, bool ResumedSavedOccurrence, long ResumeTicks,
        bool FailedStopKeptResourcesOpen, bool StopSavedBeforeDisposing, bool ClosedExactlyOnce);

    internal static void Verify(Observation actual)
    {
        Assert.True(actual.RepairKeptWorkspaceClosed);
        Assert.True(actual.CreatedDefaultFiles);
        Assert.True(actual.CachedCatalogAvailable);
        Assert.True(actual.RepeatedStartReusedSession);
        Assert.True(actual.ReopenedSameSession);
        Assert.True(actual.ResumedSavedOccurrence);
        Assert.Equal(TimeSpan.FromMilliseconds(800).Ticks, actual.ResumeTicks);
        Assert.True(actual.FailedStopKeptResourcesOpen);
        Assert.True(actual.StopSavedBeforeDisposing);
        Assert.True(actual.ClosedExactlyOnce);
    }
}
