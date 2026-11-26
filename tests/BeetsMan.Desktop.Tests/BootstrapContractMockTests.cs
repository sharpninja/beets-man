using BeetsMan.Desktop.Application.Configuration;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves startup configuration assertions before binding appsettings and database adapters.</summary>
/// <remarks>2026-11-24: Prepare the TEST-CONFIG-001 mock gate for FR-CONFIG-001#ac-defaults, ac-overrides and ac-repair.</remarks>
public sealed class BootstrapContractMockTests
{
    /// <summary>Rejects an unsupported database outcome that enters the workspace or requests replacement.</summary>
    /// <remarks>2026-11-26: Validate independently constructed repair responses for FR-CONFIG-001#ac-repair.</remarks>
    [Fact]
    public void UnsupportedSchemaAssertionsRejectActivationAndReplacement()
    {
        var database = "data/retain.db";
        var settings = new StartupFixture().ReadDefaults() with { DatabasePath = database };
        var correct = new BootstrapState(1, BootstrapStatus.RepairRequired, null, settings,
            Issues: [new("unsupported-desktop-database-schema", nameof(BootstrapSettings.DatabasePath))]);
        BootstrapAssertions.VerifySchemaRepair(correct, database);
        Reject(correct with { Status = BootstrapStatus.Ready, Effective = settings });
        Reject(correct with { CreationProposal = new(Guid.NewGuid(), database) });
        Reject(correct with { Proposed = null });
        Reject(correct with { Issues = [new("desktop-database-unavailable", nameof(BootstrapSettings.DatabasePath))] });
        Reject(correct with { Issues = [new("unsupported-desktop-database-schema", nameof(BootstrapSettings.MusicRoot))] });

        void Reject(BootstrapState bad) => Assert.ThrowsAny<XunitException>(() => BootstrapAssertions.VerifySchemaRepair(bad, database));
    }

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
