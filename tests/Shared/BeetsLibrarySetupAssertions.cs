using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies the authorized STB08 existing-library and initialization paths.</summary>
/// <remarks>2026-11-25: Pin the Open and Initialize effects for FR-BEETS-001#ac-open and TEST-BEETS-001.</remarks>
internal static class BeetsLibrarySetupAssertions
{
    internal enum Step
    {
        UnregisteredRead, OpenExisting, ReadExistingSettings, InitializeExisting, OpenUninitialized, ListenerInitialize,
        UnapprovedDestination, StaleInitialization, Initialize, RetryInitialization, OpenAfterRestart
    }

    internal sealed record Observation(int Status, long? Revision = null, bool Initialized = false, bool CanEdit = false,
        bool ConfigurationMatches = false, bool CatalogPresent = false, bool ExistingBytesPreserved = true, string? Issue = null);

    internal static async Task VerifyAsync(Func<Step, Task<Observation>> observe)
    {
        await Check(Step.UnregisteredRead, new(403));
        await Check(Step.OpenExisting, new(200, 0, Initialized: true, CanEdit: true, CatalogPresent: true));
        await Check(Step.ReadExistingSettings, new(200, 0, CanEdit: true, ConfigurationMatches: true, CatalogPresent: true));
        await Check(Step.InitializeExisting, new(409, 0, CatalogPresent: true, Issue: "beets-library-already-initialized"));
        await Check(Step.OpenUninitialized, new(200, 0, CanEdit: true));
        await Check(Step.ListenerInitialize, new(403));
        await Check(Step.UnapprovedDestination, new(400, Issue: "beets-library-destination-unapproved"));
        await Check(Step.StaleInitialization, new(409, 0, Issue: "stale-beets-library-settings"));
        var initialized = new Observation(200, 1, Initialized: true, CanEdit: true, ConfigurationMatches: true, CatalogPresent: true);
        await Check(Step.Initialize, initialized);
        await Check(Step.RetryInitialization, initialized);
        await Check(Step.OpenAfterRestart, initialized);

        async Task Check(Step step, Observation expected) => Assert.Equal(expected, await observe(step));
    }
}
