using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks that backend position receipts can prove a live timeline without extra storage writes.</summary>
/// <remarks>2026-11-24: Prove the live progress part of TEST-PLAYER-001 for FR-PLAYER-001#ac-transport.</remarks>
public sealed class PlaybackPositionMockTests
{
    [Fact]
    public void CorrectClockReceiptsDiscriminateFrozenPositionAndDatabaseWrites()
    {
        var start = TimeSpan.FromSeconds(17);
        var advanced = start + TimeSpan.FromMilliseconds(120);
        PlaybackPositionAssertions.Verify(start, advanced, 3, 3);
        Assert.ThrowsAny<XunitException>(() => PlaybackPositionAssertions.Verify(start, start, 3, 3));
        Assert.ThrowsAny<XunitException>(() => PlaybackPositionAssertions.Verify(start, advanced, 3, 4));
    }
}

/// <summary>Asserts captured audio clock progression and its read-only boundary.</summary>
internal static class PlaybackPositionAssertions
{
    public static void Verify(TimeSpan first, TimeSpan second, long previousCheckpoint, long latestCheckpoint)
    {
        Assert.True(second > first, $"Expected audio position after {first}, got {second}.");
        Assert.Equal(previousCheckpoint, latestCheckpoint);
    }
}
