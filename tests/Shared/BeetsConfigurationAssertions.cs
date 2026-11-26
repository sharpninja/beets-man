using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Verifies persisted host configuration changes and their revision and authorization boundaries.</summary>
/// <remarks>2026-11-25: Pin settings edit and native activation expectations for FR-BEETS-001#ac-open and TEST-BEETS-001.</remarks>
internal static class BeetsConfigurationAssertions
{
    internal const string OriginalNaming = "$albumartist/$album/$title";
    internal const string EditedNaming = "Sorted/$albumartist/$title";

    internal enum Step
    {
        Read, Unauthorized, DifferentCatalog, UnknownPlugin, UnsupportedMatching, Stale, Save, Replay, DifferentIntent, ReadAfterRestart, NativeSettings
    }

    internal sealed record Observation(int Status, long? Revision = null, string Detail = "", string Behavior = "",
        string Plugins = "", bool BaseYamlPreserved = true);

    internal static async Task VerifyAsync(Func<Step, Task<Observation>> send)
    {
        await Check(Step.Read, new(200, 0, OriginalNaming, "keep-tags"));
        await Check(Step.Unauthorized, new(403));
        await Check(Step.DifferentCatalog, new(400, Detail: "beets-library-destination-unapproved"));
        await Check(Step.UnknownPlugin, new(400, Detail: "beets-library-plugin-unapproved"));
        await Check(Step.UnsupportedMatching, new(400, Detail: "beets-library-import-behavior-unsupported"));
        await Check(Step.Stale, new(409, 0, "stale-beets-library-settings"));
        var updated = new Observation(200, 1, EditedNaming, "keep-tags", "info");
        await Check(Step.Save, updated);
        await Check(Step.Replay, updated);
        await Check(Step.DifferentIntent, new(409, Detail: "operation-intent-mismatch"));
        await Check(Step.ReadAfterRestart, updated);
        await Check(Step.NativeSettings, updated);

        async Task Check(Step step, Observation expected) => Assert.Equal(expected, await send(step));
    }
}
