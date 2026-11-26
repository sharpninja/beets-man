using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns ordered playlist edits and retains user input through validation or conflicts.</summary>
/// <remarks>2026-11-24: Bind the catalog playlist workflow and preserve offline drafts for FR-LIBRARY-001#ac-browse and FR-RECORDS-001#ac-offline.</remarks>
/// <remarks>2026-11-24: Preserve the authoritative backing revision when staging a local save for FR-LIBRARY-002#ac-batch-local.</remarks>
/// <remarks>2026-11-25: Present the accepted stage and clear its receipt at batch resolution for FR-LIBRARY-002#ac-batch-review and ac-batch-revert.</remarks>
public sealed partial class PlaylistDraftViewModel(PresentationServices services, Guid ownerId, PlaybackPresentation playback)
    : PresentationViewModel(services, ownerId)
{
    private PlaylistDraft? draft;
    private PlaylistSavedReceipt? saved;

    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private LibraryBatchStageReceipt? stagedSave;

    /// <summary>Gets the authoritative draft and entry order.</summary>
    public PlaylistDraft? Draft => draft;

    /// <summary>Gets the last committed playlist receipt for this draft.</summary>
    public PlaylistSavedReceipt? Saved => saved;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task NewAsync(CancellationToken token) => CreateAsync(new PlaylistDraftSource.Empty(), token);

    [RelayCommand(CanExecute = nameof(CanCopyQueue))]
    private Task CreateFromQueueAsync(CancellationToken token) => playback.State?.Queue is { } queue
        ? CreateAsync(new PlaylistDraftSource.QueueSnapshot(playback.SessionId, queue.QueueRevision, [.. queue.Entries.Select(entry => entry.TrackId)]), token)
        : Task.CompletedTask;

    [RelayCommand]
    private Task OpenAsync(Guid draftId, CancellationToken token) =>
        ReadAsync(new GetPlaylistDraft(draftId), "draft", ApplyDraft, token,
            result => result.DraftId == draftId && IsCurrentRevision(result));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task EditSavedPlaylistAsync(PlaylistVersion playlist, CancellationToken token) =>
        CreateAsync(new PlaylistDraftSource.SavedPlaylist(playlist.PlaylistId, playlist.Revision), token);

    [RelayCommand(CanExecute = nameof(CanEditEntries))]
    private Task AddSelectedAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EditAsync(new PlaylistDraftEdit.Append(tracks), token);

    [RelayCommand(CanExecute = nameof(CanEditEntries))]
    private Task DropTracksAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EditAsync(new PlaylistDraftEdit.Append(tracks), token);

    [RelayCommand(CanExecute = nameof(CanEditEntries))]
    private Task ReorderAsync(ImmutableArray<Guid> entries, CancellationToken token) => EditAsync(new PlaylistDraftEdit.Reorder(entries), token);

    [RelayCommand(CanExecute = nameof(CanEditEntries))]
    private Task RemoveTracksAsync(ImmutableArray<Guid> entries, CancellationToken token) => EditAsync(new PlaylistDraftEdit.Remove(entries), token);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync(CancellationToken token)
    {
        if (draft is not { } captured)
            return Task.CompletedTask;
        var submittedName = Name;
        return WriteAsync(new SavePlaylist(captured.DraftId, submittedName, captured.Revision, captured.BasePlaylist?.Revision), result =>
        {
            if (draft is { } current && current.DraftId == result.DraftId)
            {
                StagedSave = result.LocalBatch;
                if (result.LocalBatch is { } stage)
                {
                    Services.PresentBatchStage(stage);
                    return;
                }
                var committed = current with
                {
                    BasePlaylist = new(result.PlaylistId, result.Revision), Name = submittedName,
                    Revision = Math.Max(current.Revision, result.DraftRevision ?? current.Revision)
                };
                SetProperty(ref draft, committed, nameof(Draft));
                SetProperty(ref saved, result, nameof(Saved));
            }
        }, token, result => result.DraftId == captured.DraftId && draft?.DraftId == result.DraftId, retryInvalid: true);
    }

    /// <summary>Clears feedback for a resolved batch while keeping the operator's editable draft.</summary>
    internal void ReceiveBatchMode(LibraryBatchMode mode)
    {
        if (StagedSave is { } stage && mode.Revision > stage.Version.Revision && stage.Version.BatchId != mode.BatchId)
            StagedSave = null;
    }

    protected override void OnWriteAvailabilityChanged()
    {
        NewCommand.NotifyCanExecuteChanged(); CreateFromQueueCommand.NotifyCanExecuteChanged(); EditSavedPlaylistCommand.NotifyCanExecuteChanged();
        AddSelectedCommand.NotifyCanExecuteChanged(); DropTracksCommand.NotifyCanExecuteChanged(); ReorderCommand.NotifyCanExecuteChanged();
        RemoveTracksCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => CanWrite;
    private bool CanCopyQueue() => CanWrite && playback.State?.Queue is { Entries.IsDefaultOrEmpty: false };
    private bool CanSave() => CanWrite && draft is not null;
    private bool CanEditEntries(ImmutableArray<Guid> ids) => CanSave() && !ids.IsDefaultOrEmpty;
    private bool IsCurrentRevision(PlaylistDraft next) => draft?.DraftId != next.DraftId || next.Revision >= draft.Revision;

    private Task CreateAsync(PlaylistDraftSource source, CancellationToken token) =>
        WriteAsync(new CreatePlaylistDraft(source), ApplyDraft, token, IsCurrentRevision);

    private Task EditAsync(PlaylistDraftEdit edit, CancellationToken token) => draft is { } current
        ? WriteAsync(new EditPlaylistDraft(current.DraftId, edit, current.Revision), ApplyDraft, token,
            next => next.DraftId == current.DraftId && draft?.DraftId == next.DraftId && IsCurrentRevision(next))
        : Task.CompletedTask;

    private void ApplyDraft(PlaylistDraft next)
    {
        if (draft?.DraftId != next.DraftId)
        {
            Name = next.Name;
            SetProperty(ref saved, null, nameof(Saved));
            StagedSave = null;
        }
        else if (Name == draft.Name)
        {
            Name = next.Name;
        }
        SetProperty(ref draft, next, nameof(Draft));
        OnWriteAvailabilityChanged();
    }
}
