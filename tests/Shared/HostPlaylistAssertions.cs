using Xunit;

namespace BeetsMan.Host.Tests;

/// <summary>Asserts the observable atomic host playlist publication and ordered deliveries.</summary>
/// <remarks>2026-11-24: Define shared TEST-RECORDS-001/002 assertions for FR-RECORDS-001#ac-replicate, ac-notify and ac-offline.</remarks>
internal static class HostPlaylistAssertions
{
    public static readonly Guid EntryFirst = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    public static readonly Guid EntrySecond = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    public static readonly Guid EntryThird = Guid.Parse("a1000000-0000-0000-0000-000000000003");
    public static readonly Guid RecipientB = Guid.Parse("b1000000-0000-0000-0000-000000000002");
    public static readonly Guid RecipientC = Guid.Parse("b1000000-0000-0000-0000-000000000003");
    public static readonly IReadOnlyList<Guid> PlaylistOrder = [EntrySecond, EntryFirst, EntryThird];

    public static void Verify(ObservedPublication receipt, IReadOnlyList<Guid> order, IReadOnlyList<Guid> deliveries,
        ObservedPublication replay, long revisionAfterReplay)
    {
        Assert.NotEqual(Guid.Empty, receipt.PlaylistId);
        Assert.Equal(2, receipt.PlaylistRevision);
        Assert.Equal(2, receipt.FeedRevision);
        Assert.Equal(PlaylistOrder, order);
        Assert.Equal([RecipientB, RecipientC], deliveries.Order());
        Assert.Equal(receipt, replay);
        Assert.Equal(receipt.FeedRevision, revisionAfterReplay);
    }

    public static void VerifyRolledBack(long before, long after, bool playlistFound, bool feedEntryFound, bool notificationFound)
    {
        Assert.Equal(before, after);
        Assert.False(playlistFound);
        Assert.False(feedEntryFound);
        Assert.False(notificationFound);
    }

    public sealed record ObservedPublication(Guid PlaylistId, long PlaylistRevision, long FeedRevision);
}
