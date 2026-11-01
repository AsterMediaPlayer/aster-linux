using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Seerr;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The Discover tab: the library's Upcoming episodes, and with a Seerr
/// sign-in its own Discover page, popular films and shows, and the requests.
/// A port of the Apple app's DiscoverView. The sections show as pills under
/// the heading; without Seerr there's Upcoming alone.
/// </summary>
public sealed class DiscoverViewModel : BrowsePageViewModel
{
    public enum Section { Discover, Upcoming, Movies, Series, Requests }

    private readonly HeroModel upcomingHero;
    private Section? chosen;
    private IReadOnlyList<object> cards = [];
    /// <summary>The Movies and Series grids' filters, kept as each is left.</summary>
    private SeerrFilter movieFilter = new(SeerrKind.Movie), seriesFilter = new(SeerrKind.Tv);
    private readonly Dictionary<SeerrKind, IReadOnlyList<SeerrGenre>> genres = [];

    public DiscoverViewModel(INavigator navigator) : base(navigator)
    {
        upcomingHero = UpcomingRows.Hero(navigator);
        navigator.Seerr.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateSections();
            _ = LoadAsync();
        });
        UpdateSections();
        _ = LoadAsync();
    }

    private bool HasSeerr => Navigator.Seerr.Account is not null;

    private IReadOnlyList<Section> Available => HasSeerr ? Enum.GetValues<Section>() : [Section.Upcoming];

    private Section Shown => chosen is { } section && Available.Contains(section) ? section : Available[0];

    private void UpdateSections() => Sections = Available.Count < 2 ? [] : Available
        .Select(s => new Choice(s.ToString(), s == Shown, new RelayCommand(() =>
        {
            if (s == Shown) return;
            chosen = s;
            cards = [];
            UpdateSections();
            _ = LoadAsync();
        })))
        .ToList();

    // Movies, Series and Requests: a grid of Seerr's cards.
    protected override bool HasGrid => Shown is Section.Movies or Section.Series or Section.Requests;
    public override CardStyle GridStyle => CardStyle.Seerr;
    public override bool ShowsSort => false;
    protected override IReadOnlyList<object>? LocalCards => HasGrid ? cards : null;

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var heading = new TitleRow("Discover", this, Navigator.Seerr.NeedsSignIn
            ? "Seerr ended the session: sign in again in Settings › Requests."
            : null);
        if (Shown == Section.Upcoming) return await UpcomingRows.LoadAsync(Navigator, upcomingHero, this, heading);
        if (Navigator.Seerr.Account is not { } account) return [heading];
        var section = Shown;
        // A section's cards, only if it's still the one showing when they come.
        void Show(IReadOnlyList<object> found)
        {
            if (Shown == section) cards = found;
        }
        try
        {
            switch (section)
            {
                case Section.Movies or Section.Series:
                    var kind = section == Section.Movies ? SeerrKind.Movie : SeerrKind.Tv;
                    var filter = kind == SeerrKind.Movie ? movieFilter : seriesFilter;
                    if (!genres.ContainsKey(kind))
                    {
                        try { genres[kind] = await SeerrClient.GenresAsync(kind, account); }
                        catch (SeerrException) { genres[kind] = []; }
                    }
                    var titles = new List<SeerrTitle>();
                    // Five pages, a hundred titles.
                    for (var page = 1; page <= 5; page++)
                    {
                        var reply = await SeerrClient.TitlesAsync(filter.Path(page), kind, account);
                        titles.AddRange(reply.Results);
                        if (page >= (reply.TotalPages ?? 1)) break;
                    }
                    var found = titles.DistinctBy(t => t.Key).Select(t => (object)new SeerrCard(Navigator, t)).ToList();
                    Show(found);
                    return found.Count == 0
                        ? [heading, Filters(kind, filter), new StatusRow("Nothing matches", "Try fewer filters.")]
                        : [heading, Filters(kind, filter)];
                case Section.Requests:
                    var requests = await RequestCardsAsync(account);
                    Show(requests);
                    return requests.Count == 0
                        ? [heading, new StatusRow("No requests yet", "Films and shows asked for through Seerr show here.")]
                        : [heading, new HeadingRow("Requests")];
                default:
                    return [heading, .. await DiscoverRowsAsync(account)];
            }
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut)
        {
            Navigator.Seerr.SessionEnded();
            return [heading, new StatusRow("Signed out of Seerr", "Sign in again in Settings › Requests.")];
        }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Discover: {section} failed: {error}");
            return [heading, new StatusRow("Couldn't reach Seerr", error.Message)];
        }
        // Anything else is a fault here, not Seerr's: shown and logged, not a page left loading for ever.
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"Discover: {section} broke: {error}");
            return [heading, new StatusRow("Something went wrong", error.Message)];
        }
    }

    /// <summary>
    /// The grid's filters, as chips a controller reaches: the sort, a genre,
    /// and Anime (the Animation genre, originally in Japanese, at one press).
    /// </summary>
    private FilterRow Filters(SeerrKind kind, SeerrFilter filter)
    {
        void Set(SeerrFilter changed)
        {
            if (kind == SeerrKind.Movie) movieFilter = changed; else seriesFilter = changed;
            _ = LoadAsync();
        }
        var sorts = Enum.GetValues<SeerrSort>().Select((sort, i) => new TrackChoice(i, SeerrFilter.SortLabel(sort), sort == filter.Sort,
            new RelayCommand(() => Set(filter with { Sort = sort })))).ToList();
        // One genre at a time, besides Animation (which Anime keeps).
        var available = genres.GetValueOrDefault(kind) ?? [];
        var chosenGenre = available.FirstOrDefault(g => g.Id != SeerrFilter.AnimationGenre && filter.Genres.Contains(g.Id));
        HashSet<int> Keep(int? genre)
        {
            var kept = filter.IsAnime ? new HashSet<int> { SeerrFilter.AnimationGenre } : [];
            if (genre is { } id) kept.Add(id);
            return kept;
        }
        var genreChoices = new List<TrackChoice> { new(0, "Any Genre", chosenGenre is null, new RelayCommand(() => Set(filter with { Genres = Keep(null) }))) };
        genreChoices.AddRange(available.Where(g => !filter.IsAnime || g.Id != SeerrFilter.AnimationGenre)
            .Select(g => new TrackChoice(g.Id, g.Name, g.Id == chosenGenre?.Id, new RelayCommand(() => Set(filter with { Genres = Keep(g.Id) })))));
        return new FilterRow(
        [
            new FilterChip(SeerrFilter.SortLabel(filter.Sort), filter.Sort != SeerrSort.Popular, Choices: sorts),
            new FilterChip(chosenGenre?.Name ?? "Genre", chosenGenre is not null, Choices: genreChoices),
            new FilterChip("Anime", filter.IsAnime, new RelayCommand(() => Set(filter.WithAnime(!filter.IsAnime)))),
        ]);
    }

    /// <summary>Seerr's own Discover rows, in its admin's order and names, with In Theaters after Trending.</summary>
    private async Task<IReadOnlyList<BrowseRow>> DiscoverRowsAsync(SeerrAccount account)
    {
        var sliders = (await SeerrClient.SlidersAsync(account)).ToList();
        var trending = sliders.FindIndex(s => s.Type == 4);
        sliders.Insert(trending + 1, new SeerrSlider { Type = SeerrClient.InTheatersType });
        var rows = await Task.WhenAll(sliders.Select(slider => RowAsync(slider, account)));
        return rows.OfType<BrowseRow>().ToList();
    }

    private async Task<BrowseRow?> RowAsync(SeerrSlider slider, SeerrAccount account)
    {
        var name = SeerrClient.SliderTitle(slider);
        try
        {
            switch (slider.Type)
            {
                case 1:
                    return Posters(name, await SeerrClient.RecentlyAddedAsync(account));
                case 2:
                    var requests = await SeerrClient.RequestsAsync(SeerrRequestFilter.All, mine: false, account.UserId, 1, account);
                    return Posters(name, await SeerrClient.TitlesForAsync(requests.Results.Select(r => (r.Media.TmdbId, r.Media.MediaType ?? r.Type ?? "movie")).Distinct().ToList(), account));
                case 6 or 10:
                    var kind = slider.Type == 6 ? SeerrKind.Movie : SeerrKind.Tv;
                    var genres = await SeerrClient.GenresAsync(kind, account);
                    return Tiles(name, genres.Select(g => new SeerrTileCard($"genre-{kind}-{g.Id}", g.Name,
                        SeerrPages.Url(SeerrClient.Image(g.Backdrops?.FirstOrDefault(), "w780")), null,
                        () => OpenList(g.Name, kind == SeerrKind.Movie ? $"/discover/movies?genre={g.Id}" : $"/discover/tv?genre={g.Id}", kind))));
                case 8 or 12:
                    var companyKind = slider.Type == 8 ? SeerrKind.Movie : SeerrKind.Tv;
                    var companies = await SeerrClient.CompaniesAsync(companyKind, account);
                    return Tiles(name, companies.Select(c => new SeerrTileCard($"company-{companyKind}-{c.Id}", c.Name, null,
                        SeerrPages.Url(SeerrClient.LogoUrl(c.LogoPath)),
                        () => OpenList(c.Name, companyKind == SeerrKind.Movie ? $"/discover/movies/studio/{c.Id}" : $"/discover/tv/network/{c.Id}", companyKind))));
                case SeerrClient.InTheatersType:
                    return Posters(name, await SeerrClient.InTheatersAsync(account));
                default:
                    if (SeerrClient.TitlesPath(slider) is not { } list) return null;
                    return Posters(name, (await SeerrClient.TitlesAsync(list.Path, list.Kind, account)).Results);
            }
        }
        // One row failing (a studio Seerr can't find) leaves the others.
        catch (Exception error) when (error is SeerrException { Failure: not SeerrFailure.SignedOut } or HttpRequestException or TaskCanceledException
            or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private ShelfRow? Posters(string name, IEnumerable<SeerrTitle> titles) =>
        Shelf(name, titles.DistinctBy(t => t.Key).Select(t => new SeerrCard(Navigator, t)), CardStyle.Seerr);

    private ShelfRow? Tiles(string name, IEnumerable<SeerrTileCard> tiles) => Shelf(name, tiles, CardStyle.SeerrTile);

    /// <summary>A genre's, studio's or network's titles, as a page of their own.</summary>
    private void OpenList(string title, string path, SeerrKind kind) =>
        Navigator.Push(new SeerrListViewModel(Navigator, title, path, kind) { IsPushed = true });

    /// <summary>The requests, newest first: everyone's for someone who manages them, else the viewer's own.</summary>
    private async Task<IReadOnlyList<object>> RequestCardsAsync(SeerrAccount account)
    {
        var me = await SeerrClient.MeAsync(account);
        var requests = (await SeerrClient.RequestsAsync(SeerrRequestFilter.All, mine: !me.ManagesRequests, account.UserId, 1, account)).Results;
        // A show asked for more than once (a season at a time) is looked up once.
        var titles = await SeerrClient.TitlesForAsync(requests.Select(r => (r.Media.TmdbId, r.Media.MediaType ?? r.Type ?? "movie")).Distinct().ToList(), account);
        var byKey = titles.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
        return requests
            .Select(r => byKey.TryGetValue($"{r.Media.MediaType ?? r.Type ?? "movie"}-{r.Media.TmdbId}", out var title)
                ? (object)new SeerrCard(Navigator, title, r.StateLabel, me.ManagesRequests ? r.RequestedBy?.DisplayName : null, r)
                : null)
            .OfType<object>()
            .ToList();
    }
}

/// <summary>A genre's, studio's or network's titles from Seerr: a grid, most popular first.</summary>
public sealed class SeerrListViewModel : BrowsePageViewModel
{
    private readonly string title, path;
    private readonly SeerrKind kind;
    private IReadOnlyList<object> cards = [];

    public SeerrListViewModel(INavigator navigator, string title, string path, SeerrKind kind) : base(navigator)
    {
        (this.title, this.path, this.kind) = (title, path, kind);
        _ = LoadAsync();
    }

    protected override bool HasGrid => true;
    public override CardStyle GridStyle => CardStyle.Seerr;
    public override bool ShowsSort => false;
    protected override IReadOnlyList<object>? LocalCards => cards;

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        if (Navigator.Seerr.Account is not { } account) return [new TitleRow(title, this), new StatusRow("Signed out of Seerr")];
        try
        {
            var titles = new List<SeerrTitle>();
            for (var page = 1; page <= 3; page++)
            {
                var reply = await SeerrClient.TitlesAsync($"{path}{(path.Contains('?') ? '&' : '?')}page={page}", kind, account);
                titles.AddRange(reply.Results);
                if (page >= (reply.TotalPages ?? 1)) break;
            }
            cards = titles.DistinctBy(t => t.Key).Select(t => (object)new SeerrCard(Navigator, t)).ToList();
            return [new TitleRow(title, this)];
        }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException)
        {
            return [new TitleRow(title, this), new StatusRow("Couldn't reach Seerr", error.Message)];
        }
    }
}
