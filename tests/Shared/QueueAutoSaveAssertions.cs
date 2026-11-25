using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Playback;
using Xunit;

namespace BeetsMan.Desktop.Tests;

/// <summary>Describes observable queue edits and host playlist writes for the two auto-save branches.</summary>
/// <remarks>2026-11-24: TEST-PLAYER-001 for FR-PLAYER-001#ac-queue-autosave, with FR-RECORDS-001#ac-offline replay coverage.</remarks>
internal static class QueueAutoSaveAssertions
{
    public static void Verify(ImmutableArray<Guid> before, QueueSnapshot enabledEdit, QueueSnapshot disabledEdit,
        ImmutableArray<Guid> hostOrder, int logicalHostWrites)
    {
        Assert.Equal(3, before.Length);
        Assert.Equal([before[2], before[0], before[1]], enabledEdit.Entries.Select(entry => entry.EntryId));
        Assert.True(enabledEdit.AutoSave);
        Assert.Equal(QueueSaveStatus.Saved, enabledEdit.SaveStatus);
        Assert.NotNull(enabledEdit.SaveReceipt);
        Assert.Equal(8, enabledEdit.BackingPlaylist?.Revision);
        Assert.Equal([before[2], before[0]], disabledEdit.Entries.Select(entry => entry.EntryId));
        Assert.False(disabledEdit.AutoSave);
        Assert.Equal(QueueSaveStatus.NotRequested, disabledEdit.SaveStatus);
        Assert.Null(disabledEdit.SaveReceipt);
        Assert.Equal([before[2], before[0], before[1]], hostOrder);
        Assert.Equal(1, logicalHostWrites);
    }

    public static void VerifyReplay(Guid first, Guid second, IReadOnlyList<(Guid OperationId, long Revision)> attempted,
        QueueSnapshot queue, IEnumerable<Guid> hostEntries)
    {
        Assert.Equal([(first, 7L), (second, 8L)], attempted);
        Assert.Equal(QueueSaveStatus.Saved, queue.SaveStatus);
        Assert.Equal(9, queue.BackingPlaylist?.Revision);
        Assert.Equal(queue.Entries.Select(entry => entry.EntryId), hostEntries);
    }
}
