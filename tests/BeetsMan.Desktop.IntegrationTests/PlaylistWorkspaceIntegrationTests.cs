using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Verifies the real playlist workspace against a disposable LiteDB file and controlled host responses.</summary>
/// <remarks>2026-11-24: Bind stable offline playlist replay for FR-RECORDS-001#ac-offline and TEST-RECORDS-002.</remarks>
public sealed class PlaylistWorkspaceIntegrationTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-draft-");
    private readonly CancellationToken stop = TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReorderedDraftAndPendingSaveSurviveRestartThenReplayExactlyOnce()
    {
        var path = Path.Combine(directory.FullName, "desktop.db");
        var origin = Guid.NewGuid();
        var first = Guid.NewGuid();
        var other = Guid.NewGuid();
        var session = Guid.NewGuid();
        var host = new RecordingHost();
        var saveKey = Guid.NewGuid();
        SavePlaylist command;
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var router = Router(store, host, origin);
            var draft = Get(await router.SendAsync(new CreatePlaylistDraft(new PlaylistDraftSource.QueueSnapshot(session, 7, [first, first, other])),
                Context(), stop));
            Assert.Equal(3, draft.Entries.Select(item => item.EntryId).Distinct().Count());
            var change = new EditPlaylistDraft(draft.DraftId,
                new PlaylistDraftEdit.Reorder([draft.Entries[0].EntryId, draft.Entries[2].EntryId, draft.Entries[1].EntryId]), draft.Revision);
            var reordered = Get(await router.SendAsync(change, Context(), stop));
            Assert.Equal([first, other, first], reordered.Entries.Select(entry => entry.TrackId));
            var stale = await router.SendAsync(change, Context(), stop);
            Assert.Equal(reordered.Revision, Assert.IsType<Outcome<PlaylistDraft>.Conflict>(stale).ActualRevision);

            command = new(draft.DraftId, "Offline Mix", reordered.Revision, null);
            var result = await router.SendAsync(command, Context(saveKey), stop);
            Assert.Equal("host-offline", Assert.IsType<Outcome<PlaylistSavedReceipt>.DependencyFailure>(result).Code);
            PlaylistWorkspaceAssertions.VerifyPending(Assert.Single(host.Attempts), new(origin, saveKey), [first, other, first]);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var reopenedRouter = Router(reopened, host, origin);
        var fetched = Get(await reopenedRouter.QueryAsync(new GetPlaylistDraft(command.DraftId), Context().Request, stop));
        Assert.Equal("Offline Mix", fetched.Name);
        Assert.Equal([first, other, first], fetched.Entries.Select(entry => entry.TrackId));

        host.Online = true;
        var receipt = Get(await reopenedRouter.SendAsync(command, Context(saveKey), stop));
        PlaylistWorkspaceAssertions.VerifyPending(host.Attempts[1], new(origin, saveKey), [first, other, first]);
        Assert.Equal(fetched.Revision + 1, receipt.DraftRevision);
        Assert.Equal(receipt, Get(await reopenedRouter.SendAsync(command, Context(saveKey), stop)));
        Assert.Equal(2, host.Attempts.Count);

        var conflict = await reopenedRouter.SendAsync(command with { Name = "Changed intent" }, Context(saveKey), stop);
        Assert.Equal("operation-intent-mismatch", Assert.IsType<Outcome<PlaylistSavedReceipt>.Conflict>(conflict).Code);
        Assert.Equal(2, host.Attempts.Count);
        var committed = Get(await reopenedRouter.QueryAsync(new GetPlaylistDraft(command.DraftId), Context().Request, stop));
        Assert.Equal(new PlaylistVersion(receipt.PlaylistId, receipt.Revision), committed.BasePlaylist);
        Assert.Equal(receipt.DraftRevision, committed.Revision);
    }

    [Fact]
    public async Task ValidationDefersAnyHostEffectAndPreservesTheDraft()
    {
        var path = Path.Combine(directory.FullName, "validation.db");
        using var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true);
        var host = new RecordingHost();
        var router = Router(store, host, Guid.NewGuid());
        var draft = Get(await router.SendAsync(new CreatePlaylistDraft(new PlaylistDraftSource.Empty()), Context(), stop));
        var result = await router.SendAsync(new SavePlaylist(draft.DraftId, "  ", draft.Revision, null), Context(), stop);
        Assert.Equal("Name", Assert.Single(Assert.IsType<Outcome<PlaylistSavedReceipt>.Invalid>(result).Issues).Field);
        Assert.Empty(host.Attempts);
        var preserved = Get(await router.QueryAsync(new GetPlaylistDraft(draft.DraftId), Context().Request, stop));
        Assert.Equal(draft.Name, preserved.Name);
        Assert.Equal(draft.Revision, preserved.Revision);
    }

    public void Dispose() => directory.Delete(recursive: true);

    private static RequestDispatcher Router(LiteDbDesktopPlaylistStore store, RecordingHost host, Guid origin) =>
        new PlaylistWorkspaceHandlers(store, store, new EmptyCatalog(), host, origin).Register(new()).Build(typeof(SavePlaylist));

    private static CommandContext Context(Guid? operation = null) => new(new(Guid.NewGuid(), null), operation ?? Guid.NewGuid());

    private static T Get<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;

    private sealed class EmptyCatalog : IPublishedPlaylistReader
    {
        public Task<Outcome<PublishedPlaylist>> ReadAsync(Guid playlistId, CancellationToken cancellationToken) =>
            Task.FromResult<Outcome<PublishedPlaylist>>(new Outcome<PublishedPlaylist>.Invalid([new("not-found")]));
    }

    private sealed class RecordingHost : IPlaylistPublicationHost
    {
        private readonly Guid playlist = Guid.NewGuid();
        public List<PendingPlaylistSave> Attempts { get; } = [];
        public bool Online { get; set; }

        public Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken cancellationToken)
        {
            Attempts.Add(operation);
            return Task.FromResult<Outcome<PlaylistSavedReceipt>>(Online
                ? new Outcome<PlaylistSavedReceipt>.Success(new(operation.DraftId, playlist, 1))
                : new Outcome<PlaylistSavedReceipt>.DependencyFailure("host-offline", true));
        }
    }
}
