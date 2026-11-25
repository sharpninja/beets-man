using Xunit;
using Xunit.Sdk;
using static BeetsMan.Host.Tests.HostPlaylistAssertions;

namespace BeetsMan.Host.Tests;

/// <summary>Exercises independent host result and rollback fixtures before binding persistence.</summary>
/// <remarks>2026-11-24: Prove publication and retention assertions for TEST-RECORDS-001 and TEST-RECORDS-002.</remarks>
public sealed class HostPlaylistMockTests
{
    [Fact]
    public void AssertionsRejectReorderedEntriesDuplicatePublicationAndMissingRecipient()
    {
        ObservedPublication receipt = new(Guid.NewGuid(), 2, 2);
        Verify(receipt, PlaylistOrder, [RecipientB, RecipientC], receipt, 2);
        Assert.ThrowsAny<XunitException>(() => Verify(receipt, [EntryFirst, EntrySecond, EntryThird], [RecipientB, RecipientC], receipt, 2));
        Assert.ThrowsAny<XunitException>(() => Verify(receipt, PlaylistOrder, [RecipientB], receipt, 2));
        Assert.ThrowsAny<XunitException>(() => Verify(receipt, PlaylistOrder, [RecipientB, RecipientC], receipt with { FeedRevision = 3 }, 3));
    }

    [Fact]
    public void RollbackAssertionsRejectEveryPartialPublication()
    {
        VerifyRolledBack(3, 3, false, false, false);
        Assert.ThrowsAny<XunitException>(() => VerifyRolledBack(3, 3, true, false, false));
        Assert.ThrowsAny<XunitException>(() => VerifyRolledBack(3, 3, false, true, false));
        Assert.ThrowsAny<XunitException>(() => VerifyRolledBack(3, 3, false, false, true));
        Assert.ThrowsAny<XunitException>(() => VerifyRolledBack(3, 4, false, false, false));
    }
}
