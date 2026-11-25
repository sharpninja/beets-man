using BeetsMan.Desktop.Application.Configuration;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves startup configuration assertions before binding appsettings and database adapters.</summary>
/// <remarks>2026-11-24: Prepare the TEST-CONFIG-001 mock gate for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed class BootstrapContractMockTests
{
    [Fact]
    public void DefaultAndRepairAssertionsRejectUnapprovedDatabaseCreation()
    {
        var fixture = new StartupFixture();
        BootstrapAssertions.VerifyDefaults(fixture.ReadDefaults());
        var approval = fixture.Propose("custom.db");
        BootstrapAssertions.VerifyProposal(approval, "custom.db");
        Assert.ThrowsAny<XunitException>(() => BootstrapAssertions.VerifyProposal(approval with { Status = BootstrapStatus.Ready }, "custom.db"));
        Assert.ThrowsAny<XunitException>(() => BootstrapAssertions.VerifyProposal(approval, "other.db"));
    }

    private sealed class StartupFixture
    {
        public BootstrapSettings ReadDefaults() => new("appsettings.json", "data/beets-man.db", "Music",
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("Default", "Information").Add("Microsoft", "Warning"));

        public BootstrapState Propose(string database) => new(1, BootstrapStatus.CreationApprovalRequired, null,
            ReadDefaults() with { DatabasePath = database }, new(Guid.NewGuid(), database));
    }
}
