using BeetsMan.Desktop.Application.Library;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Checks stable operation identity and ordered tracks in both mock and real playlist paths.</summary>
/// <remarks>2026-11-24: Share TEST-RECORDS-001 offline replay assertions with the persistent fixture.</remarks>
internal static class PlaylistWorkspaceAssertions
{
    public static void VerifyPending(PendingPlaylistSave proposed, OperationKey key, Guid[] trackOrder)
    {
        Assert.Equal(PendingPlaylistSaveStatus.Pending, proposed.Status);
        Assert.Equal(key, proposed.Key);
        Assert.Equal(trackOrder, proposed.Entries.Select(entry => entry.TrackId));
        Assert.Null(proposed.Receipt);
    }
}
