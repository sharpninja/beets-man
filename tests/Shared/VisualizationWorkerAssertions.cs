using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks selection, exact routed values, and stale-frame and worker-loss behavior.</summary>
/// <remarks>2026-11-25: Independent inputs for FR-PLUGINS-001#ac-visualize, ac-recovery and TEST-PLUGINS-002.</remarks>
internal static class VisualizationWorkerAssertions
{
    internal sealed record Observation(string BuiltIn, long? BuiltInSequence, string Plugin, long? PluginSequence,
        IReadOnlyList<float> PluginValues, bool StaleWasProjected, string DetachedReason, bool TransportResponsive);

    internal static void Verify(Observation actual)
    {
        Assert.Equal("waveform", actual.BuiltIn);
        Assert.True(actual.BuiltInSequence is > 0);
        Assert.Equal("example.visualizer/1.0.0/levels", actual.Plugin);
        Assert.True(actual.PluginSequence > actual.BuiltInSequence);
        Assert.Equal(new[] { 0.125f, 0.5f, 0.875f }, actual.PluginValues);
        Assert.False(actual.StaleWasProjected);
        Assert.Equal("plugin-worker-timeout", actual.DetachedReason);
        Assert.True(actual.TransportResponsive);
    }
}
