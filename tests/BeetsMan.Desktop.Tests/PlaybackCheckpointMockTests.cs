using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;
using static BeetsMan.Desktop.Tests.PlaybackCheckpointAssertions;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks playback restart assertions with a valid fixture and deliberately altered state.</summary>
/// <remarks>2026-11-24: Prove the playback checkpoint branch of TEST-PLAYER-001 before persistence bindings.</remarks>
public sealed class PlaybackCheckpointMockTests
{
    [Fact]
    public void CheckpointAssertionsDistinguishOccurrenceAndSeekState()
    {
        Verify(Session, 2, 4, Entries, Entries[1].EntryId, TimeSpan.FromSeconds(42), true, RepeatMode.One);
        Assert.ThrowsAny<XunitException>(() => Verify(Session, 2, 4, Entries, Entries[0].EntryId, TimeSpan.FromSeconds(42), true, RepeatMode.One));
        Assert.ThrowsAny<XunitException>(() => Verify(Session, 2, 4, Entries, Entries[1].EntryId, TimeSpan.Zero, true, RepeatMode.One));
        Assert.ThrowsAny<XunitException>(() => Verify(Session, 2, 4, Entries, Entries[1].EntryId, TimeSpan.FromSeconds(42), false, RepeatMode.Off));
    }
}
