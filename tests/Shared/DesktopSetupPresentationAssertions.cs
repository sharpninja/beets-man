using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the first-run setup binding and the handoff to the active desktop.</summary>
/// <remarks>2026-11-25: Cover FR-CONFIG-001#ac-defaults, ac-overrides, ac-repair and TEST-CONFIG-002.</remarks>
internal static class DesktopSetupPresentationAssertions
{
    internal sealed record Evidence(bool FirstRunWaitedForChoice, bool DatabaseApprovalMatchedPath, bool WaitedDuringWorkspaceActivation,
        bool OpenedRequestedPaths, bool CursorReleased, bool RetryReusedWorkspace, bool ExistingSettingsRequireRestart,
        bool SetupOwnersDisposed);

    internal static void Verify(Evidence result)
    {
        Assert.True(result.FirstRunWaitedForChoice);
        Assert.True(result.DatabaseApprovalMatchedPath);
        Assert.True(result.WaitedDuringWorkspaceActivation);
        Assert.True(result.OpenedRequestedPaths);
        Assert.True(result.CursorReleased);
        Assert.True(result.RetryReusedWorkspace);
        Assert.True(result.ExistingSettingsRequireRestart);
        Assert.True(result.SetupOwnersDisposed);
    }
}
