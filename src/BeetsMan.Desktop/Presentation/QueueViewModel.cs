using System.Collections.Immutable;
using System.ComponentModel;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Owns queue selection and edits for the active playback session.</summary>
/// <remarks>2026-11-24: Bind queue edits and report authoritative auto-save outcomes under FR-PLAYER-001#ac-queue-autosave.</remarks>
public sealed partial class QueueViewModel : PresentationViewModel
{
    private readonly PlaybackPresentation playback;

    [ObservableProperty] private ImmutableArray<Guid> selectedEntryIds = [];
    [ObservableProperty] private QueueEdit? pendingEdit;

    /// <summary>Creates the queue editor over the shared playback session.</summary>
    public QueueViewModel(PresentationServices services, PlaybackPresentation playback) : base(services, playback.SessionId)
    {
        this.playback = playback;
        playback.PropertyChanged += OnPlaybackChanged;
    }

    /// <summary>Gets the queue, including its optional playlist and latest auto-save receipt.</summary>
    public QueueSnapshot? Queue => playback.State?.Queue;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadPlaylistAsync(PlaylistVersion playlist, CancellationToken token) =>
        Send(new LoadPlaylistQueue(playback.SessionId, playlist.PlaylistId, playlist.Revision), token);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ReorderAsync(ImmutableArray<Guid> ids, CancellationToken token) => EditAsync(new QueueEdit.Reorder(ids), token);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RemoveAsync(ImmutableArray<Guid> ids, CancellationToken token) => EditAsync(new QueueEdit.Remove(ids), token);

    [RelayCommand(CanExecute = nameof(CanSetAutoSave))]
    private Task SetAutoSaveAsync(bool enabled, CancellationToken token) => Queue is { BackingPlaylist: not null } queue
        ? Send(new SetQueueAutoSave(playback.SessionId, enabled, queue.QueueRevision), token) : Task.CompletedTask;

    protected override void OnDisposed() => playback.PropertyChanged -= OnPlaybackChanged;

    protected override void OnWriteAvailabilityChanged()
    {
        LoadPlaylistCommand.NotifyCanExecuteChanged();
        ReorderCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        SetAutoSaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanLoad() => CanWrite;
    private bool CanEdit(ImmutableArray<Guid> ids) => CanWrite && Queue is not null && !ids.IsDefaultOrEmpty;
    private bool CanSetAutoSave() => CanWrite && Queue?.BackingPlaylist is not null;

    private Task EditAsync(QueueEdit edit, CancellationToken token)
    {
        if (!CanWrite || Queue is not { } queue)
            return Task.CompletedTask;
        PendingEdit = edit;
        return WriteAsync(new EditQueue(playback.SessionId, edit, queue.QueueRevision, queue.BackingPlaylist?.Revision), result =>
        {
            playback.Apply(result);
            if (result.Queue.SaveStatus != QueueSaveStatus.Conflict)
                PendingEdit = null;
        }, token, playback.Accepts);
    }

    private Task Send(ICommand<PlaybackState> request, CancellationToken token) => WriteAsync(request, playback.Apply, token, playback.Accepts);

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Queue is { } queue)
            SelectedEntryIds = [.. SelectedEntryIds.Where(id => queue.Entries.Any(entry => entry.EntryId == id))];
        OnPropertyChanged(nameof(Queue));
        OnWriteAvailabilityChanged();
    }
}
