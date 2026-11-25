using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Captures the displayed criteria and cursor for one catalog read.</summary>
public sealed record CatalogBrowseInput(CatalogCategory Category, CatalogFilter Filter, CatalogSort Sort, string? Page = null);

/// <summary>Captures the displayed revision for a favorite edit.</summary>
public sealed record FavoriteEdit(Guid TrackId, bool IsFavorite, long Revision);

/// <summary>Owns the Library's catalog, selection, and explicit playback gestures.</summary>
/// <remarks>2026-11-24: Bind FR-LIBRARY-001#ac-browse and ac-offline, plus the offline favorite intent in FR-RECORDS-001#ac-offline.</remarks>
public sealed partial class LibraryViewModel(PresentationServices services, Guid ownerId, PlaybackPresentation playback)
    : PresentationViewModel(services, ownerId)
{
    private CatalogPage? page;
    private TrackAvailabilitySnapshot? availability;
    private ImmutableDictionary<Guid, FavoriteState> favorites = ImmutableDictionary<Guid, FavoriteState>.Empty;

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private ImmutableArray<Guid> selectedIds = [];

    /// <summary>Gets the latest visible catalog results and criteria.</summary>
    public CatalogPage? Page => page;

    /// <summary>Gets availability from the matching catalog selection.</summary>
    public TrackAvailabilitySnapshot? Availability => availability;

    /// <summary>Gets the favorite values received for tracks in this scope.</summary>
    public ImmutableDictionary<Guid, FavoriteState> Favorites => favorites;

    protected override Guid WriteOwner => playback.SessionId;

    [RelayCommand]
    private Task BrowseAsync(CatalogBrowseInput input, CancellationToken token) =>
        LoadAsync(input.Category, input.Filter, input.Sort, input.Page, token);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SearchAsync(string? text, CancellationToken token)
    {
        SearchText = text ?? string.Empty;
        return LoadAsync(CatalogCategory.Search, new CatalogFilter(Text: SearchText), CurrentSort, null, token);
    }

    [RelayCommand]
    private Task ClearSearchAsync(CancellationToken token) => SearchAsync(string.Empty, token);

    [RelayCommand]
    private Task OpenArtistAsync(Guid artistId, CancellationToken token) =>
        LoadAsync(CatalogCategory.Albums, new CatalogFilter(ArtistId: artistId), CurrentSort, null, token);

    [RelayCommand]
    private Task OpenAlbumAsync(Guid albumId, CancellationToken token) =>
        LoadAsync(CatalogCategory.Tracks, new CatalogFilter(AlbumId: albumId), new CatalogSort(CatalogSortKey.TrackNumber), null, token);

    [RelayCommand]
    private Task RefreshAvailabilityAsync(ImmutableArray<Guid> tracks, CancellationToken token) =>
        ReadAsync(new GetTrackAvailability(tracks), "availability", next => SetProperty(ref availability, next, nameof(Availability)), token,
            next => next.Revision >= (page?.Revision ?? 0) && next.Revision >= (availability?.Revision ?? 0));

    [RelayCommand]
    private Task SetFavoriteAsync(FavoriteEdit input, CancellationToken token) =>
        WriteAsync(new SetFavorite(input.TrackId, input.IsFavorite, input.Revision), result =>
            SetProperty(ref favorites, Favorites.SetItem(result.TrackId, result), nameof(Favorites)), token,
            result => result.TrackId == input.TrackId && result.Revision >= (Favorites.GetValueOrDefault(input.TrackId)?.Revision ?? 0),
            writeOwner: Scope.OwnerId);

    [RelayCommand(CanExecute = nameof(CanEnqueueMany))]
    private Task PlaySelectionAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EnqueueAsync(tracks, EnqueueMode.PlayNow, token);

    [RelayCommand(CanExecute = nameof(CanEnqueueOne))]
    private Task PlayTrackAsync(Guid trackId, CancellationToken token) => EnqueueAsync([trackId], EnqueueMode.PlayNow, token);

    [RelayCommand(CanExecute = nameof(CanEnqueueOne))]
    private Task PlaySearchResultAsync(Guid trackId, CancellationToken token) => EnqueueAsync([trackId], EnqueueMode.PlayNow, token);

    [RelayCommand(CanExecute = nameof(CanEnqueueMany))]
    private Task PlayPlaylistAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EnqueueAsync(tracks, EnqueueMode.PlayNow, token);

    [RelayCommand(CanExecute = nameof(CanEnqueueMany))]
    private Task QueueAlbumAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EnqueueAsync(tracks, EnqueueMode.Append, token);

    [RelayCommand(CanExecute = nameof(CanEnqueueMany))]
    private Task AddToQueueAsync(ImmutableArray<Guid> tracks, CancellationToken token) => EnqueueAsync(tracks, EnqueueMode.Append, token);

    protected override void OnWriteAvailabilityChanged()
    {
        PlaySelectionCommand.NotifyCanExecuteChanged(); PlayTrackCommand.NotifyCanExecuteChanged();
        PlaySearchResultCommand.NotifyCanExecuteChanged(); PlayPlaylistCommand.NotifyCanExecuteChanged();
        QueueAlbumCommand.NotifyCanExecuteChanged(); AddToQueueCommand.NotifyCanExecuteChanged();
    }

    private CatalogSort CurrentSort => page?.Sort ?? new CatalogSort(CatalogSortKey.TrackNumber);
    private bool CanEnqueueMany(ImmutableArray<Guid> tracks) => CanWrite && !tracks.IsDefaultOrEmpty && tracks.Any(IsPlayable);
    private bool CanEnqueueOne(Guid trackId) => CanWrite && IsPlayable(trackId);

    private bool IsPlayable(Guid id)
    {
        if (id == Guid.Empty)
            return false;
        var status = Availability?.Tracks.GetValueOrDefault(id) ??
            Page?.Rows.FirstOrDefault(row => row.Id == id)?.Availability ?? TrackAvailability.Unknown;
        return status == TrackAvailability.Downloaded || status == TrackAvailability.Unknown && Page?.IsOffline != true;
    }

    private Task LoadAsync(CatalogCategory category, CatalogFilter filter, CatalogSort sort, string? cursor, CancellationToken token) =>
        ReadAsync(new BrowseCatalog(category, filter, sort, cursor), "catalog", ApplyCatalog, token,
            result => result.Category == category && result.Filter == filter && result.Sort == sort && result.Revision >= (page?.Revision ?? 0));

    private void ApplyCatalog(CatalogPage incoming)
    {
        SetProperty(ref page, incoming, nameof(Page));
        SelectedIds = [.. SelectedIds.Where(id => incoming.Rows.Any(row => row.Id == id))];
        SetProperty(ref availability, null, nameof(Availability));
        OnWriteAvailabilityChanged();
    }

    private Task EnqueueAsync(ImmutableArray<Guid> tracks, EnqueueMode mode, CancellationToken token)
    {
        if (!CanEnqueueMany(tracks))
            return Task.CompletedTask;
        return WriteAsync(new EnqueueTracks(playback.SessionId, [.. tracks.Where(IsPlayable)], mode), playback.Apply, token, playback.Accepts);
    }
}
