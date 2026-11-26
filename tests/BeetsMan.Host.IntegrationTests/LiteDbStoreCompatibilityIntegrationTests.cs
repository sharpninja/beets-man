using Xunit;

namespace BeetsMan.Host.IntegrationTests;

/// <summary>Verifies legacy optional fields through actual host reads and edits.</summary>
/// <remarks>2026-11-26: Bind TEST-RECORDS-002 and TEST-FILESYNC-002 to TR-RECORDS-ARCH-001#ac-litedb-c4-entities and FR-FILESYNC-001#ac-safety.</remarks>
public sealed class LiteDbStoreCompatibilityIntegrationTests
{
    /// <summary>Prunes legacy playlist-only audio after removing the last playlist that references it.</summary>
    [Fact]
    public async Task MissingIsDirectRemainsPlaylistDependency()
    {
        using var fixture = new HostDevicePlaylistIntegrationTests();
        await fixture.VerifyMissingIsDirectRemainsPlaylistDependency();
    }
}
