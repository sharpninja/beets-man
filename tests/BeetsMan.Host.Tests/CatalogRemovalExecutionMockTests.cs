using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Proves the removal assertions reject inexact native changes, unintended file effects, and early publication.</summary>
/// <remarks>2026-11-24: Establish independent mock evidence for TEST-BEETS-002 and FR-BEETS-001#ac-edit and ac-recover.</remarks>
public sealed class CatalogRemovalExecutionMockTests
{
    /// <summary>Checks all expected phases and rejects a changed observation for each assertion.</summary>
    [Fact]
    public void RejectsEarlyPublicationAndRepeatedOrBroadenedRemoval()
    {
        var job = Guid.Parse("cc3e37cd-3b2c-47f4-8742-a9037c9ac936");
        var operation = Guid.Parse("b4173662-e27c-4824-af9e-3c21f441480b");
        var peer = Guid.Parse("c04b4056-7c36-47fa-9761-c1cbc36a9eb7");
        var effect = new CatalogRemovalExecutionAssertions.Evidence(job, job, operation, operation, "Running", ["Inbox/Keep.flac", "Inbox/Take [2].flac"],
            42, 0, "Published", 43, 1, ["Track:Delete:Inbox/Take [1],.flac", "Track:Delete:Inbox/Take [2].flac"], true,
            ["Inbox/Keep.flac"], ["Inbox/Take [1],.flac", "Inbox/Take [2].flac"], true, true, peer, [peer],
            "Failed", "maintenance-native-track-changed", false);
        CatalogRemovalExecutionAssertions.Verify(effect);
        CatalogRemovalExecutionAssertions.Evidence[] rejected =
        [
            effect with { Job = Guid.Empty, RecoveredJob = Guid.Empty },
            effect with { RecoveredJob = peer },
            effect with { Operation = Guid.Empty, PublishedOperation = Guid.Empty },
            effect with { PublishedOperation = peer },
            effect with { InterruptedPhase = "Published" },
            effect with { NativeAfterInterruption = ["Inbox/Keep.flac"] },
            effect with { InterruptedFeed = 43 },
            effect with { InterruptedDeliveries = 1 },
            effect with { RecoveredPhase = "Running" },
            effect with { PublishedFeed = 44 },
            effect with { Publications = 2 },
            effect with { Tombstones = ["Track:Delete:Inbox/Take [2].flac"] },
            effect with { ScopedTombstones = false },
            effect with { NativeAfterRecovery = [] },
            effect with { AppliedPaths = [effect.AppliedPaths[0], effect.AppliedPaths[0], effect.AppliedPaths[1]] },
            effect with { AllAudioPreserved = false },
            effect with { ExactHostTrackRemoval = false },
            effect with { QueuedPeers = [peer, job] },
            effect with { DivergedPhase = "Published" },
            effect with { DivergedCode = null },
            effect with { DivergedRetryable = true }
        ];
        Assert.All(rejected, result => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CatalogRemovalExecutionAssertions.Verify(result)));
    }
}
