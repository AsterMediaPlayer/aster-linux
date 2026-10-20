using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>One page of Home's featured banner.</summary>
public sealed partial class HeroPage(INavigator navigator, BaseItem item) : ObservableObject
{
    public BaseItem Item { get; } = item;
    public Uri? Backdrop { get; } = navigator.Client.BackdropUrl(item, width: 1920);
    public Uri? Logo { get; } = navigator.Client.LogoUrl(item);
    public string Title => Item.Kind == ItemKind.Episode ? Item.SeriesName ?? Item.DisplayTitle : Item.DisplayTitle;
    public string? EpisodeLine => Item.Kind == ItemKind.Episode ? string.Join(" · ", new[] { Item.EpisodeCode, Item.Name }.OfType<string>()) : null;
    public MetadataLine Metadata { get; } = MetadataLine.For(item);
    public string? Overview => Item.Overview;
    public string PlayLabel => Item.CanResume ? "Resume" : Item.Kind == ItemKind.Series ? "Watch Now" : "Play";
    public string? Remaining => Item.RemainingText is { } left ? $"·  {left}" : null;

    /// <summary>The page opens.</summary>
    [RelayCommand] private void Open() => navigator.Open(Item);

    /// <summary>
    /// Play and Resume: the title's page opens and starts playing at once,
    /// so closing the player leaves the viewer on that page.
    /// </summary>
    [RelayCommand] private void Play() => navigator.Open(Item, playOnOpen: true);
}

/// <summary>
/// The Apple TV app's Home: a featured banner, then shelves of what to
/// watch: Continue Watching, Up Next, the newest movies, shows and anime,
/// and one shelf for each other library.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IRefreshable
{
    private readonly INavigator navigator;
    private IReadOnlyList<BaseItem>? animeLibraries;
    private bool refreshWanted;

    public HomeViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        _ = LoadAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero), nameof(HeroDots), nameof(HasNextHero))]
    public partial IReadOnlyList<HeroPage> Featured { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeroDots), nameof(HasPreviousHero), nameof(HasNextHero))]
    public partial int HeroIndex { get; set; }

    public bool HasPreviousHero => HeroIndex > 0;
    public bool HasNextHero => HeroIndex < Featured.Count - 1;

    [ObservableProperty] public partial IReadOnlyList<Shelf> Shelves { get; set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorMessage { get; set; }

    public bool HasHero => Featured.Count > 0;

    /// <summary>The banner's page dots: true for the page showing.</summary>
    public IReadOnlyList<bool> HeroDots => Featured.Select((_, i) => i == HeroIndex).ToList();

    [RelayCommand] private void NextHero() => HeroIndex = Featured.Count == 0 ? 0 : Math.Min(HeroIndex + 1, Featured.Count - 1);
    [RelayCommand] private void PreviousHero() => HeroIndex = Math.Max(HeroIndex - 1, 0);

    public void Refresh() => _ = LoadAsync();

    /// <summary>Refreshes the next time Home is shown.</summary>
    public void RefreshLater() => refreshWanted = true;

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        refreshWanted = false;
        IsLoading = Featured.Count == 0;
        ErrorMessage = null;
        var client = navigator.Client;
        try
        {
            var resume = client.ResumeAsync(limit: 20);
            var next = client.NextUpAsync(limit: 20);
            var libraries = await client.UserViewsAsync();
            // Anime has its own row, so the Movies and TV rows skip its libraries.
            animeLibraries ??= await client.AnimeLibrariesAsync(libraries);
            var animeIds = animeLibraries.Select(l => l.Id).ToHashSet();
            var moviesLibrary = libraries.FirstOrDefault(l => l.CollectionType == "movies" && !animeIds.Contains(l.Id))?.Id;
            var showsLibrary = libraries.FirstOrDefault(l => l.CollectionType == "tvshows" && !animeIds.Contains(l.Id))?.Id;
            var movies = OrEmpty(client.LatestAsync(moviesLibrary, 20, "Movie"));
            var shows = OrEmpty(client.LatestAsync(showsLibrary, 20, "Series"));
            var anime = RecentlyAddedAnimeAsync(client, animeLibraries);
            var others = libraries
                .Where(l => l.CollectionType is not ("movies" or "tvshows" or "music" or "playlists" or "livetv" or "books" or "photos"))
                .Select(async l => (Library: l, Items: await OrEmpty(client.LatestAsync(l.Id, 20))))
                .ToList();

            var resumeItems = await resume;
            var nextItems = (await next).Where(e => resumeItems.All(r => r.Id != e.Id)).ToList();
            var movieItems = await movies;
            var showItems = await shows;

            var shelves = new List<Shelf>();
            void Add(string title, IEnumerable<BaseItem> items)
            {
                var cards = items.Select(i => ItemCard.Landscape(navigator, i)).ToList();
                if (cards.Count > 0) shelves.Add(new Shelf(title, cards));
            }
            Add("Continue Watching", resumeItems);
            Add("Up Next", nextItems);
            Add("Recently Added Movies", movieItems);
            Add("Recently Added TV Shows", showItems);
            Add("Recently Added Anime", await anime);
            foreach (var (library, items) in await Task.WhenAll(others)) Add(library.DisplayTitle, items);

            var picks = new List<BaseItem>();
            foreach (var item in resumeItems.Take(3).Concat(nextItems.Take(3)).Concat(movieItems.Take(4)).Concat(showItems.Take(3)))
            {
                if (picks.All(p => p.Id != item.Id)) picks.Add(item);
            }
            var featured = picks.Take(8).Select(item => new HeroPage(navigator, item)).ToList();
            // Stay on the page the viewer was on if that title is still featured.
            var current = HeroIndex < Featured.Count ? Featured[HeroIndex].Item.Id : null;
            Featured = featured;
            HeroIndex = Math.Max(0, featured.FindIndex(p => p.Item.Id == current));
            Shelves = shelves;
            // The banner's pictures are big and slow: all of them, ahead.
            ImageLoader.Prefetch(featured.SelectMany(p => new[] { p.Backdrop, p.Logo }));
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            else ErrorMessage = error.Message;
        }
        IsLoading = false;
    }

    /// <summary>Called as Home comes back into view.</summary>
    public void Shown()
    {
        if (refreshWanted) Refresh();
    }

    private static async Task<IReadOnlyList<BaseItem>> OrEmpty(Task<List<BaseItem>> request)
    {
        try { return await request; }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { return []; }
    }

    /// <summary>
    /// The newest across the anime libraries. Jellyfin reports a season when
    /// new episodes land in it; its series is shown instead, and each show once.
    /// </summary>
    private static async Task<IReadOnlyList<BaseItem>> RecentlyAddedAnimeAsync(JellyfinClient client, IReadOnlyList<BaseItem> libraries)
    {
        var all = (await Task.WhenAll(libraries.Select(l => OrEmpty(client.LatestAsync(l.Id, 20))))).SelectMany(x => x);
        var newest = all.OrderByDescending(i => i.DateCreated ?? "", StringComparer.Ordinal).ToList();
        var seriesIds = newest.Where(i => i.Kind == ItemKind.Season).Select(i => i.SeriesId).OfType<string>().Distinct().ToList();
        var series = new Dictionary<string, BaseItem>();
        if (seriesIds.Count > 0)
        {
            try
            {
                foreach (var found in (await client.ItemsAsync(new() { ["ids"] = string.Join(',', seriesIds) })).Items) series.TryAdd(found.Id, found);
            }
            catch (Exception error) when (error is JellyfinException or HttpRequestException or TimeoutException) { }
        }
        var shown = new List<BaseItem>();
        foreach (var item in newest)
        {
            var entry = item.Kind == ItemKind.Season && item.SeriesId is { } id && series.TryGetValue(id, out var show) ? show : item;
            if (shown.All(s => s.Id != entry.Id)) shown.Add(entry);
        }
        return shown.Take(20).ToList();
    }
}
