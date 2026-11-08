using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The Apple TV app's Home: a featured banner, then shelves of what to
/// watch: Continue Watching, Up Next, the newest movies, shows and anime,
/// and one shelf for each other library.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;
    private bool refreshWanted;

    public HomeViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Hero = new HeroModel(navigator);
        _ = LoadAsync();
    }

    public HeroModel Hero { get; }

    [ObservableProperty] public partial IReadOnlyList<Shelf> Shelves { get; set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public void Refresh() => _ = LoadAsync();

    /// <summary>Refreshes the next time Home is shown.</summary>
    public void RefreshLater() => refreshWanted = true;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    /// <summary>Home as it was last shown, for this server and person: what a start shows at once.</summary>
    private sealed record HomeSnapshot(IReadOnlyList<HomeSnapshotShelf> Shelves, IReadOnlyList<BaseItem> Hero);

    private sealed record HomeSnapshotShelf(string Title, IReadOnlyList<BaseItem> Items);

    private string SnapshotKey => Aster.Core.Snapshots.Key(navigator.Client.BaseUrl, navigator.Client.UserId, "home");

    private void Show(HomeSnapshot home)
    {
        Hero.Show(home.Hero);
        Shelves = home.Shelves
            .Select(s => new Shelf(s.Title, s.Items.Select(i => ItemCard.Landscape(navigator, i)).ToList()))
            .Where(s => s.Cards.Count > 0)
            .ToList();
    }

    private async Task LoadAsync()
    {
        refreshWanted = false;
        ErrorMessage = null;
        // Last time's Home at once, then what the server says now in its place.
        if (!Hero.HasPages && Shelves.Count == 0 && await Aster.Core.Snapshots.ReadAsync<HomeSnapshot>(SnapshotKey) is { } kept
            && !Hero.HasPages && Shelves.Count == 0)
            Show(kept);
        IsLoading = !Hero.HasPages && Shelves.Count == 0;
        var client = navigator.Client;
        try
        {
            var resume = client.ResumeAsync(limit: 20);
            var next = client.NextUpAsync(limit: 20);
            var libraries = await client.UserViewsAsync();
            // Anime has its own row, so the Movies and TV rows skip its libraries.
            var animeLibraries = (await navigator.Anime).Libraries;
            var animeIds = animeLibraries.Select(l => l.Id).ToHashSet();
            var moviesLibrary = libraries.FirstOrDefault(l => l.CollectionType == "movies" && !animeIds.Contains(l.Id))?.Id;
            var showsLibrary = libraries.FirstOrDefault(l => l.CollectionType == "tvshows" && !animeIds.Contains(l.Id))?.Id;
            var movies = OrEmpty(client.LatestAsync(moviesLibrary, 20, "Movie"));
            var shows = OrEmpty(client.LatestAsync(showsLibrary, 20, "Series"));
            var anime = AnimeCatalog.RecentlyAddedAsync(client, animeLibraries);
            var others = libraries
                .Where(l => l.CollectionType is not ("movies" or "tvshows" or "music" or "playlists" or "livetv" or "books" or "photos"))
                .Select(async l => (Library: l, Items: await OrEmpty(client.LatestAsync(l.Id, 20))))
                .ToList();

            var resumeItems = await resume;
            var nextItems = (await next).Where(e => resumeItems.All(r => r.Id != e.Id)).ToList();
            var movieItems = await movies;
            var showItems = await shows;

            var shelves = new List<HomeSnapshotShelf>();
            void Add(string title, IEnumerable<BaseItem> items) => shelves.Add(new HomeSnapshotShelf(title, items.ToList()));
            Add("Continue Watching", resumeItems);
            Add("Up Next", nextItems);
            Add("Recently Added Movies", movieItems);
            Add("Recently Added TV Shows", showItems);
            Add("Recently Added Anime", await anime);
            foreach (var (library, items) in await Task.WhenAll(others)) Add(library.DisplayTitle, items);

            var home = new HomeSnapshot(shelves, resumeItems.Take(3).Concat(nextItems.Take(3)).Concat(movieItems.Take(4)).Concat(showItems.Take(3)).ToList());
            Show(home);
            _ = Aster.Core.Snapshots.WriteAsync(SnapshotKey, home);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            // Last time's Home stays up when the server can't be reached; the error shows only over nothing.
            else if (Shelves.Count == 0 && !Hero.HasPages) ErrorMessage = error.Message;
            else Aster.Core.Diagnostics.DebugLog.Note("Home", $"Couldn't refresh Home: {error.Message}");
        }
        IsLoading = false;
    }

    /// <summary>Called as Home comes back into view.</summary>
    public void Shown()
    {
        if (refreshWanted) Refresh();
    }

    private static Task<IReadOnlyList<BaseItem>> OrEmpty(Task<IReadOnlyList<BaseItem>> request) => AnimeCatalog.OrEmpty(request);
}
