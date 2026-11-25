using BeetsMan.Desktop.Application.Configuration;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Shares the proved startup assertions between the mock and real-file fixtures.</summary>
/// <remarks>2026-11-24: Reuse TEST-CONFIG-001 assertions in the actual configuration verification.</remarks>
internal static class BootstrapAssertions
{
    public static void VerifyDefaults(BootstrapSettings settings)
    {
        Assert.Equal("Information", settings.LogLevels["Default"]);
        Assert.Equal("Warning", settings.LogLevels["Microsoft"]);
    }

    public static void VerifyProposal(BootstrapState state, string database)
    {
        Assert.Equal(BootstrapStatus.CreationApprovalRequired, state.Status);
        Assert.Equal(database, state.CreationProposal?.DatabasePath);
        Assert.Equal(database, state.Proposed?.DatabasePath);
        Assert.NotEqual(Guid.Empty, state.CreationProposal?.ProposalId ?? Guid.Empty);
    }
}
