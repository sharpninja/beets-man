using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using Xunit;
using Xunit.Sdk;

namespace BeetsMan.Desktop.Tests;

/// <summary>Verifies the distinguishing queue auto-save and offline ordering assertions using independent receipts.</summary>
/// <remarks>2026-11-24: Prove TEST-PLAYER-001 for FR-PLAYER-001#ac-queue-autosave before binding the real queue store.</remarks>
public sealed class QueueAutoSaveMockTests
{
    /// <summary>Rejects missing names, mismatched playlist identities, and incorrect switch availability.</summary>
    /// <remarks>2026-11-25: Prove the presentation assertions for FR-PLAYER-001#ac-queue-autosave before binding them.</remarks>
    [Fact]
    public void BackingReceiptDistinguishesTheLoadedPlaylistFromAnUnbackedQueue()
    {
        var empty = new QueueBackingReport(null, null, false, false);
        var loaded = new QueueBackingReport(new(Guid.NewGuid(), 7), "Fixture", true, false);
        var switched = loaded with { AutoSave = true };

        QueueAutoSaveAssertions.VerifyBacking(empty, new(null, null, false, false));
        QueueAutoSaveAssertions.VerifyBacking(loaded, new(loaded.Playlist, "Fixture", true, false));
        QueueAutoSaveAssertions.VerifyBacking(switched, new(loaded.Playlist, "Fixture", true, true));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyBacking(empty, empty with { CanToggle = true }));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyBacking(loaded, loaded with { Name = null }));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyBacking(loaded, loaded with { Playlist = new(Guid.NewGuid(), 7) }));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyBacking(switched, loaded));
    }

    [Fact]
    public void ReceiptsDistinguishLocalEditsFromCommittedPlaylistWrites()
    {
        var playlist = Guid.NewGuid();
        var track = Guid.NewGuid();
        ImmutableArray<QueueEntry> entries = [new(Guid.NewGuid(), track), new(Guid.NewGuid(), track), new(Guid.NewGuid(), Guid.NewGuid())];
        var before = entries.Select(entry => entry.EntryId).ToImmutableArray();
        var online = new QueueSnapshot(3, [entries[2], entries[0], entries[1]], true, new(playlist, 8),
            QueueSaveStatus.Saved, new(Guid.Empty, playlist, 8));
        var offline = new QueueSnapshot(4, [entries[2], entries[0]], false, new(playlist, 8));
        ImmutableArray<Guid> saved = [before[2], before[0], before[1]];

        QueueAutoSaveAssertions.Verify(before, online, offline, saved, 1);
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.Verify(before, online with { SaveReceipt = null }, offline, saved, 1));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.Verify(before, online, offline, [before[2], before[0]], 2));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.Verify(before, online, offline, saved, 2));
    }

    [Fact]
    public void ReplayReceiptsDiscriminateWrongOrderAndGuessedRevision()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        var entry = new QueueEntry(Guid.NewGuid(), Guid.NewGuid());
        var snapshot = new QueueSnapshot(5, [entry], true, new(playlist, 9), QueueSaveStatus.Saved, new(Guid.Empty, playlist, 9));
        QueueAutoSaveAssertions.VerifyReplay(first, second, [(first, 7L), (second, 8L)], snapshot, [entry.EntryId]);
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyReplay(first, second, [(first, 7L), (second, 7L)], snapshot, [entry.EntryId]));
        Assert.ThrowsAny<XunitException>(() => QueueAutoSaveAssertions.VerifyReplay(first, second, [(second, 7L), (first, 8L)], snapshot, [entry.EntryId]));
    }
}
