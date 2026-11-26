using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks that diagnostics uses the effective directory at each configuration stage.</summary>
/// <remarks>2026-11-25: Pin the SCR03 handoff and restart behavior for FR-CONFIG-001#ac-logs and ac-overrides.</remarks>
internal static class LogFolderAssertions
{
    internal sealed record Handoff(string Path, bool CreateIfMissing);
    internal sealed record Observation(Handoff[] Handoffs, string BeforeRestartReceipt, string AfterRestartReceipt,
        string Failure, bool FailedHandoffHasNoReceipt, bool CancelledHandoffWasNotStarted);

    internal static void Verify(Observation actual, string defaultFolder, string configuredFolder)
    {
        Assert.Equal(new[] { new Handoff(defaultFolder, true), new Handoff(configuredFolder, true), new Handoff(configuredFolder, true) },
            actual.Handoffs);
        Assert.Equal(defaultFolder, actual.BeforeRestartReceipt);
        Assert.Equal(configuredFolder, actual.AfterRestartReceipt);
        Assert.Equal("log-folder-open-failed", actual.Failure);
        Assert.True(actual.FailedHandoffHasNoReceipt);
        Assert.True(actual.CancelledHandoffWasNotStarted);
    }
}
