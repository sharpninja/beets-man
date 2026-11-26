using Xunit;
using Xunit.Sdk;
using static BeetsMan.Host.Tests.BeetsConfigurationAssertions;

namespace BeetsMan.Host.Tests;

/// <summary>Proves that the settings fixture rejects incorrect authorization, revisions and applied values.</summary>
/// <remarks>2026-11-25: Run before the FR-BEETS-001#ac-open implementation for TEST-BEETS-001.</remarks>
public sealed class BeetsConfigurationMockTests
{
    [Fact]
    public async Task ConfigurationAssertionsDetectWrongAuthorizationAndActivation()
    {
        var changed = new Observation(200, 1, EditedNaming, "keep-tags", "info");
        var observations = new Dictionary<Step, Observation>
        {
            [Step.Read] = new(200, 0, OriginalNaming, "keep-tags"),
            [Step.Unauthorized] = new(403),
            [Step.DifferentCatalog] = new(400, Detail: "beets-library-destination-unapproved"),
            [Step.UnknownPlugin] = new(400, Detail: "beets-library-plugin-unapproved"),
            [Step.UnsupportedMatching] = new(400, Detail: "beets-library-import-behavior-unsupported"),
            [Step.Stale] = new(409, 0, "stale-beets-library-settings"),
            [Step.Save] = changed,
            [Step.Replay] = changed,
            [Step.DifferentIntent] = new(409, Detail: "operation-intent-mismatch"),
            [Step.ReadAfterRestart] = changed,
            [Step.NativeSettings] = changed
        };
        await VerifyAsync(step => Task.FromResult(observations[step]));
        foreach (var (step, result) in observations)
            await Reject(step, result.Revision.HasValue ? result with { Revision = result.Revision + 1 } :
                result with { Status = 200 });
        await Reject(Step.Save, changed with { BaseYamlPreserved = false });
        await Reject(Step.NativeSettings, changed with { Detail = OriginalNaming });
        await Reject(Step.NativeSettings, changed with { Plugins = "" });
        await Reject(Step.ReadAfterRestart, changed with { Behavior = "" });

        Task Reject(Step key, Observation wrong) =>
            Assert.ThrowsAnyAsync<XunitException>(() => VerifyAsync(step => Task.FromResult(step == key ? wrong : observations[step])));
    }
}
