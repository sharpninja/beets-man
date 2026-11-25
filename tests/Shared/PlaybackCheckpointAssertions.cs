using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Defines observable queue and seek checkpoints for tests of the playback owner.</summary>
/// <remarks>2026-11-24: Establish independent TEST-PLAYER-001/002 expectations for FR-PLAYER-001#ac-transport.</remarks>
internal static class PlaybackCheckpointAssertions
{
    public static readonly Guid Session = Guid.Parse("71000000-0000-0000-0000-000000000001");
    public static readonly Guid Track = Guid.Parse("72000000-0000-0000-0000-000000000001");
    public static readonly ImmutableArray<QueueEntry> Entries =
    [
        new(Guid.Parse("73000000-0000-0000-0000-000000000001"), Track),
        new(Guid.Parse("73000000-0000-0000-0000-000000000002"), Track)
    ];

    public static void Verify(Guid session, long checkpointRevision, long queueRevision, ImmutableArray<QueueEntry> entries,
        Guid? activeEntry, TimeSpan position, bool shuffle, RepeatMode repeat)
    {
        Assert.Equal(Session, session);
        Assert.Equal(2, checkpointRevision);
        Assert.Equal(4, queueRevision);
        Assert.Equal(Entries.AsEnumerable(), entries);
        Assert.Equal(Entries[1].EntryId, activeEntry);
        Assert.Equal(TimeSpan.FromSeconds(42), position);
        Assert.True(shuffle);
        Assert.Equal(RepeatMode.One, repeat);
    }
}
