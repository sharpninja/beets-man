using Xunit;
using Xunit.Sdk;
using static BeetsMan.Host.Tests.BeetsLibrarySetupAssertions;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises incorrect responses and native effects before binding the beets setup workflow.</summary>
/// <remarks>2026-11-25: Prove the independent acceptance harness for TEST-BEETS-001.</remarks>
/// <remarks>2026-11-26: Reject invalid authorization, locations and library initialization for FR-BEETS-001#ac-open.</remarks>
public sealed class BeetsLibrarySetupMockTests
{
    [Fact]
    public async Task Open_MatchesContract()
    {
        var active = new Observation(200, 1, Initialized: true, CanEdit: true, ConfigurationMatches: true, CatalogPresent: true);
        var scripted = new Dictionary<Step, Observation>
        {
            [Step.UnregisteredRead] = new(403),
            [Step.OpenExisting] = new(200, 0, Initialized: true, CanEdit: true, CatalogPresent: true),
            [Step.ReadExistingSettings] = new(200, 0, CanEdit: true, ConfigurationMatches: true, CatalogPresent: true),
            [Step.InitializeExisting] = new(409, 0, CatalogPresent: true, Issue: "beets-library-already-initialized"),
            [Step.OpenUninitialized] = new(200, 0, CanEdit: true),
            [Step.ListenerInitialize] = new(403),
            [Step.UnapprovedDestination] = new(400, Issue: "beets-library-destination-unapproved"),
            [Step.StaleInitialization] = new(409, 0, Issue: "stale-beets-library-settings"),
            [Step.Initialize] = active,
            [Step.RetryInitialization] = active,
            [Step.OpenAfterRestart] = active
        };
        await VerifyAsync(step => Task.FromResult(scripted[step]));
        foreach (var (stepWithFault, correct) in scripted)
        {
            var wrong = correct with { Status = correct.Status == 200 ? 403 : 200 };
            await Assert.ThrowsAnyAsync<XunitException>(() => VerifyAsync(step => Task.FromResult(step == stepWithFault ? wrong : scripted[step])));
        }
        await Reject(Step.InitializeExisting, scripted[Step.InitializeExisting] with { ExistingBytesPreserved = false });
        await Reject(Step.ReadExistingSettings, scripted[Step.ReadExistingSettings] with { ConfigurationMatches = false });
        await Reject(Step.ListenerInitialize, new Observation(403, CatalogPresent: true));
        await Reject(Step.StaleInitialization, scripted[Step.StaleInitialization] with { CatalogPresent = true });
        await Reject(Step.Initialize, active with { ConfigurationMatches = false });
        await Reject(Step.Initialize, active with { CatalogPresent = false });
        await Reject(Step.RetryInitialization, active with { Revision = 2 });
        await Reject(Step.OpenAfterRestart, active with { Initialized = false });

        Task Reject(Step key, Observation wrong) => Assert.ThrowsAnyAsync<XunitException>(
            () => VerifyAsync(step => Task.FromResult(step == key ? wrong : scripted[step])));
    }
}
