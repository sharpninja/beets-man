using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Proves offline playlist and replay assertions before binding application handlers.</summary>
/// <remarks>2026-11-24: Prepare the TEST-RECORDS-001 gate for FR-RECORDS-001#ac-offline and FR-LIBRARY-001#ac-browse.</remarks>
public sealed class PlaylistWorkspaceMockTests
{
    [Fact]
    public void PendingOperationsPreserveOriginalIdentityAndExactTrackOrder()
    {
        var key = new OperationKey(Guid.NewGuid(), Guid.NewGuid());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var fixture = new OfflineReplyFixture(new PendingPlaylistSave(key, Guid.NewGuid(), 3, null, "Mix",
            [new(Guid.NewGuid(), first), new(Guid.NewGuid(), second), new(Guid.NewGuid(), first)], PendingPlaylistSaveStatus.Pending));
        var result = fixture.Submit();
        Assert.IsType<Outcome<PlaylistSavedReceipt>.DependencyFailure>(result);
        var recorded = fixture.Observed!;
        PlaylistWorkspaceAssertions.VerifyPending(recorded, key, [first, second, first]);
        Assert.ThrowsAny<XunitException>(() => PlaylistWorkspaceAssertions.VerifyPending(recorded, key with { OperationId = Guid.NewGuid() }, [first, second, first]));
        Assert.ThrowsAny<XunitException>(() => PlaylistWorkspaceAssertions.VerifyPending(recorded, key, [second, first, first]));
    }

    private sealed class OfflineReplyFixture(PendingPlaylistSave pending)
    {
        public PendingPlaylistSave? Observed { get; private set; }

        public Outcome<PlaylistSavedReceipt> Submit()
        {
            Observed = pending;
            return new Outcome<PlaylistSavedReceipt>.DependencyFailure("host-offline", true);
        }
    }
}
