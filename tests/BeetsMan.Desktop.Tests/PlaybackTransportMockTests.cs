using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks the approved transport sequence against independently scripted receipts.</summary>
/// <remarks>2026-11-24: Prove TEST-PLAYER-001 for FR-PLAYER-001#ac-transport before binding queue and checkpoint commands.</remarks>
public sealed class PlaybackTransportMockTests
{
    [Fact]
    public void ReceiptsDiscriminateDuplicateOccurrencesAndPausedTransport()
    {
        var a = PlaybackTransportAssertions.FirstTrack;
        var b = PlaybackTransportAssertions.SecondTrack;
        var first = new QueueEntry(Guid.NewGuid(), a);
        var duplicate = new QueueEntry(Guid.NewGuid(), a);
        var last = new QueueEntry(Guid.NewGuid(), b);
        ImmutableArray<QueueEntry> order = [first, duplicate, last];
        var reports = new PlaybackTransportReport(order, first.EntryId, duplicate.EntryId, a, last.EntryId, b, duplicate.EntryId,
            TransportStatus.Paused, TimeSpan.FromSeconds(13), true, RepeatMode.All);

        PlaybackTransportAssertions.Verify(reports);
        Assert.ThrowsAny<XunitException>(() => PlaybackTransportAssertions.Verify(reports with { AfterNext = first.EntryId }));
        Assert.ThrowsAny<XunitException>(() => PlaybackTransportAssertions.Verify(reports with { AfterPrevious = last.EntryId }));
        Assert.ThrowsAny<XunitException>(() => PlaybackTransportAssertions.Verify(reports with { AfterPause = TransportStatus.Playing }));
        Assert.ThrowsAny<XunitException>(() => PlaybackTransportAssertions.Verify(reports with { Queue = [first, last] }));
    }
}

/// <summary>Captures transport results at the command boundaries used by both fixtures.</summary>
internal sealed record PlaybackTransportReport(ImmutableArray<QueueEntry> Queue, Guid WhenStarted, Guid AfterNext,
    Guid TrackAfterNext, Guid AfterSecondNext, Guid TrackAfterSecondNext, Guid AfterPrevious, TransportStatus AfterPause,
    TimeSpan Position, bool Shuffle, RepeatMode Repeat);

/// <summary>Defines observable transport expectations independently of the playback owner.</summary>
internal static class PlaybackTransportAssertions
{
    public static readonly Guid FirstTrack = Guid.Parse("92000000-0000-0000-0000-000000000001");
    public static readonly Guid SecondTrack = Guid.Parse("92000000-0000-0000-0000-000000000002");

    public static void Verify(PlaybackTransportReport report)
    {
        Assert.Equal([FirstTrack, FirstTrack, SecondTrack], report.Queue.Select(entry => entry.TrackId));
        Assert.Equal(3, report.Queue.Select(entry => entry.EntryId).Distinct().Count());
        Assert.Equal(report.Queue[0].EntryId, report.WhenStarted);
        Assert.Equal(report.Queue[1].EntryId, report.AfterNext);
        Assert.Equal(FirstTrack, report.TrackAfterNext);
        Assert.Equal(report.Queue[2].EntryId, report.AfterSecondNext);
        Assert.Equal(SecondTrack, report.TrackAfterSecondNext);
        Assert.Equal(report.Queue[1].EntryId, report.AfterPrevious);
        Assert.Equal(TransportStatus.Paused, report.AfterPause);
        Assert.Equal(TimeSpan.FromSeconds(13), report.Position);
        Assert.True(report.Shuffle);
        Assert.Equal(RepeatMode.All, report.Repeat);
    }
}
