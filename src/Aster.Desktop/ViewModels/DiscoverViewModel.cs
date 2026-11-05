using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
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

    private Section? chosen;
    private IReadOnlyList<object> cards = [];
    /// <summary>The films a library has in 4K: left out of Discover.</summary>
    private IReadOnlySet<int> uhd = new HashSet<int>();
    /// <summary>The Movies and Series grids' filters, kept as each is left.</summary>
    private SeerrFilter movieFilter = new(SeerrKind.Movie), seriesFilter = new(SeerrKind.Tv);
    private readonly Dictionary<SeerrKind, IReadOnlyList<SeerrGenre>> genres = [];
    private readonly Dictionary<SeerrKind, IReadOnlyList<SeerrCompany>> companies = [];
    /// <summary>Requests' filters: which state, and (for whoever manages them) everyone's or one's own.</summary>
    private SeerrRequestFilter requestFilter = SeerrRequestFilter.All;
    private bool onlyMine;
    /// <summary>Upcoming's switches: anime, and other shows.</summary>
    private bool upcomingAnime = true, upcomingTV = true, upcomingFilms = true;

    public DiscoverViewModel(INavigator navigator) : base(navigator)
    {
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

    // Movies and Series: a grid of Seerr's cards. (Requests is a list of rows.)
    protected override bool HasGrid => Shown is Section.Movies or Section.Series;
    public override CardStyle GridStyle => CardStyle.Seerr;
    public override bool ShowsSort => false;
    protected override IReadOnlyList<object>? LocalCards => HasGrid ? cards : null;

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        var heading = new TitleRow("Discover", this, Navigator.Seerr.NeedsSignIn
            ? "Requests couldn't be reached through your server just now."
            : null) { Segmented = true };
        if (Shown == Section.Upcoming)
        {
            Ambient = null;
            return await UpcomingAgenda.LoadAsync(Navigator, heading, upcomingAnime, upcomingTV, upcomingFilms, (anime, tv, films) =>
            {
                (upcomingAnime, upcomingTV, upcomingFilms) = (anime, tv, films);
                _ = LoadAsync();
            });
        }
        Ambient = null;
        if (Navigator.Seerr.Account is not { } account) return [heading];
        var section = Shown;
        uhd = await SeerrPages.Uhd4kFilmsAsync(Navigator);
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
                    // Seerr's own studios (films) or networks (shows), for their menu.
                    if (!companies.ContainsKey(kind)) companies[kind] = await SeerrClient.CompaniesAsync(kind, account);
                    var titles = new List<SeerrTitle>();
                    // Five pages, a hundred titles.
                    for (var page = 1; page <= 5; page++)
                    {
                        var reply = await SeerrClient.TitlesAsync(filter.Path(page), kind, account);
                        titles.AddRange(reply.Results);
                        if (page >= (reply.TotalPages ?? 1)) break;
                    }
                    var found = titles.DistinctBy(t => t.Key).Not4k(uhd).Select(t => (object)new SeerrCard(Navigator, t)).ToList();
                    Show(found);
                    return found.Count == 0
                        ? [heading, Filters(kind, filter), new StatusRow("Nothing matches", "Try fewer filters.")]
                        : [heading, Filters(kind, filter)];
                case Section.Requests:
                    return [heading, .. await RequestRowsAsync(account)];
                default:
                    return [heading, .. await DiscoverRowsAsync(account)];
            }
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut)
        {
            Navigator.Seerr.SessionEnded();
            return [heading, new StatusRow("Requests aren't available", "Your server couldn't reach Seerr. Try again in a moment.")];
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
        var thisYear = DateTime.Now.Year;
        var years = new (string Title, int? From, int? To)[]
        {
            ("Any Year", null, null), ($"{thisYear}", thisYear, thisYear), ($"{thisYear - 1}", thisYear - 1, thisYear - 1),
            ("2020s", 2020, 2029), ("2010s", 2010, 2019), ("2000s", 2000, 2009), ("1990s", 1990, 1999), ("1980s", 1980, 1989), ("Before 1980", null, 1979),
        };
        var ratings = new (string Title, double? At)[] { ("Any Rating", null), ("6+", 6), ("7+", 7), ("8+", 8) };
        var lengths = kind == SeerrKind.Movie
            ? new (string Title, int? From, int? To)[] { ("Any Length", null, null), ("Under 90 min", null, 89), ("90 min to 2 h", 90, 120), ("Over 2 h", 121, null) }
            : new (string Title, int? From, int? To)[] { ("Any Length", null, null), ("Under 30 min", null, 29), ("30 to 60 min", 30, 60), ("Over 60 min", 61, null) };
        var languages = new (string Title, string? Code)[]
        {
            ("Any Language", null), ("English", "en"), ("Japanese", "ja"), ("Korean", "ko"), ("Spanish", "es"), ("French", "fr"),
            ("German", "de"), ("Italian", "it"), ("Chinese", "zh"), ("Hindi", "hi"),
        };
        var companies = this.companies.GetValueOrDefault(kind) ?? [];
        var anyCompany = kind == SeerrKind.Movie ? "Any Studio" : "Any Network";
        var year = years.FirstOrDefault(y => y.From == filter.FromYear && y.To == filter.ToYear);
        var rating = ratings.FirstOrDefault(r => r.At == filter.MinimumRating);
        var length = lengths.FirstOrDefault(l => l.From == filter.MinimumRuntime && l.To == filter.MaximumRuntime);
        var language = languages.FirstOrDefault(l => l.Code == filter.Language);
        List<TrackChoice> Menu<T>(IEnumerable<T> options, Func<T, string> title, Func<T, bool> chosen, Func<T, SeerrFilter> pick) =>
            options.Select((o, i) => new TrackChoice(i, title(o), chosen(o), new RelayCommand(() => Set(pick(o))))).ToList();
        return new FilterRow(
        [
            new FilterChip("Anime", filter.IsAnime, new RelayCommand(() => Set(filter.WithAnime(!filter.IsAnime))), Icon: "✦"),
            new FilterChip(SeerrFilter.SortLabel(filter.Sort), filter.Sort != SeerrSort.Popular, Choices: sorts, Icon: "⇅"),
            new FilterChip(chosenGenre?.Name ?? "Any Genre", chosenGenre is not null, Choices: genreChoices),
            new FilterChip(year.Title ?? "Any Year", filter.FromYear is not null || filter.ToYear is not null,
                Choices: Menu(years, y => y.Title, y => y.From == filter.FromYear && y.To == filter.ToYear, y => filter with { FromYear = y.From, ToYear = y.To })),
            new FilterChip(rating.Title ?? "Any Rating", filter.MinimumRating is not null,
                Choices: Menu(ratings, r => r.Title, r => r.At == filter.MinimumRating, r => filter with { MinimumRating = r.At })),
            new FilterChip(length.Title ?? "Any Length", filter.MinimumRuntime is not null || filter.MaximumRuntime is not null,
                Choices: Menu(lengths, l => l.Title, l => l.From == filter.MinimumRuntime && l.To == filter.MaximumRuntime, l => filter with { MinimumRuntime = l.From, MaximumRuntime = l.To })),
            // Anime sets Japanese itself; another language chosen turns it off.
            new FilterChip(language.Title ?? "Any Language", filter.Language is not null && !filter.IsAnime,
                Choices: Menu(languages, l => l.Title, l => l.Code == filter.Language, l => filter with { Language = l.Code })),
            new FilterChip(filter.Company?.Name ?? anyCompany, filter.Company is not null,
                Choices: Menu(new SeerrCompany?[] { null }.Concat(companies), c => c?.Name ?? anyCompany, c => c?.Id == filter.Company?.Id, c => filter with { Company = c })),
        ]);
    }

    /// <summary>The Discover page the server built for this person, once it's come (kept for this page's life).</summary>
    private Aster.Core.Tracking.DiscoverHome? home;

    private Task<Aster.Core.Tracking.DiscoverHome?>? homeLoading;

    /// <summary>
    /// The server's Discover page for this person (its Aster Tracking plugin) with Seerr's own rows: what they're
    /// watching, Recommended for You, Because You Watched, On Your Server and their watchlists first; then Seerr's rows,
    /// the server's Trending in place of Seerr's (and This Season's anime after it), and without Seerr's Watchlist,
    /// which is Plex's only. Seerr's rows don't wait for the server's page: if it isn't there within a second and a
    /// half, they show alone and the page fills in when it comes.
    /// </summary>
    private async Task<IReadOnlyList<BrowseRow>> DiscoverRowsAsync(SeerrAccount account)
    {
        homeLoading ??= LoadHomeAsync();
        var sliders = (await SeerrClient.SlidersAsync(account)).ToList();
        var seerrRows = Task.WhenAll(sliders.Select(slider => RowAsync(slider, account)));
        if (home is null && await Task.WhenAny(homeLoading, Task.Delay(1500)) == homeLoading) home = await homeLoading;
        if (home is null)
        {
            var waiting = homeLoading;
            _ = waiting.ContinueWith(done =>
            {
                if (done.Result is { } built) Avalonia.Threading.Dispatcher.UIThread.Post(() => { home = built; Refresh(); });
            }, TaskScheduler.Default);
        }

        var rows = new List<(SeerrSlider? Slider, BrowseRow? Row)>();
        var fromSeerr = await seerrRows;
        var serverRows = (home?.Rows ?? []).ToDictionary(r => r.Id, r => Shelf(r));
        var serverHas = (home?.Rows ?? []).Select(r => r.Kind).ToHashSet();
        IEnumerable<BrowseRow?> Server(params string[] kinds) =>
            (home?.Rows ?? []).Where(r => kinds.Contains(r.Kind)).Select(r => serverRows[r.Id]);

        var result = new List<BrowseRow?>();
        result.AddRange(Server("continue", "recommended", "because", "library", "watchlist"));
        for (var i = 0; i < sliders.Count; i++)
        {
            var slider = sliders[i];
            if (slider.Type == 3 && serverHas.Contains("watchlist")) continue;
            if (slider.Type == 4 && serverHas.Contains("trending"))
            {
                result.AddRange(Server("trending", "season"));
                result.Add(await RowAsync(new SeerrSlider { Type = SeerrClient.InTheatersType }, account));
                continue;
            }
            result.Add(fromSeerr[i]);
            if (slider.Type == 4) result.Add(await RowAsync(new SeerrSlider { Type = SeerrClient.InTheatersType }, account));
        }
        // No Trending among Seerr's rows: the server's go last.
        if (!sliders.Any(s => s.Type == 4)) result.AddRange(Server("trending", "season"));
        return result.OfType<BrowseRow>().ToList();
    }

    /// <summary>The server's page, or null without the plugin (or when it can't be reached).</summary>
    private async Task<Aster.Core.Tracking.DiscoverHome?> LoadHomeAsync()
    {
        if (Navigator.Jellyfin is not { } jellyfin) return null;
        try { return await Aster.Core.Tracking.AsterTracking.HomeAsync(jellyfin); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Discover: the server's page failed: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// One of the server's rows as Seerr cards: each opens Seerr's page by its TMDB id (titles without one are left
    /// out), with the row's poster (the tracking site's, or the server's own for its titles) and, for Watching, how
    /// far along it is.
    /// </summary>
    private ShelfRow? Shelf(Aster.Core.Tracking.DiscoverRow row)
    {
        var server = Navigator.Jellyfin?.BaseUrl;
        var cards = row.Items.Where(i => i.TmdbId is not null).DistinctBy(i => (i.TmdbId, i.MediaType)).Select(i =>
        {
            var movie = i.MediaType == "movie";
            var title = new SeerrTitle
            {
                Id = i.TmdbId!.Value,
                MediaType = movie ? "movie" : "tv",
                Title = movie ? i.Title : null,
                Name = movie ? null : i.Title,
                ReleaseDate = movie && i.Year is { } y ? $"{y}" : null,
                FirstAirDate = !movie && i.Year is { } f ? $"{f}" : null,
                Overview = i.Overview,
            };
            var subtitle = i.Progress ?? string.Join(" · ", new[] { i.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture), movie ? "Film" : i.Anime ? "Anime" : "Series" }.OfType<string>());
            Uri? poster = null;
            if (Uri.TryCreate(i.Poster, UriKind.Absolute, out var absolute)) poster = absolute;
            else if (i.Poster is { Length: > 0 } relative && server is not null) poster = new Uri(server, relative);
            return (object)new SeerrCard(Navigator, title, badge: i.InLibrary is not null ? "On Server" : null, subtitle: subtitle, poster: poster);
        }).ToList();
        return Shelf(row.Title, cards, CardStyle.Seerr);
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
        Shelf(name, titles.DistinctBy(t => t.Key).Not4k(uhd).Select(t => new SeerrCard(Navigator, t)), CardStyle.Seerr);

    private ShelfRow? Tiles(string name, IEnumerable<SeerrTileCard> tiles) => Shelf(name, tiles, CardStyle.SeerrTile);

    /// <summary>A genre's, studio's or network's titles, as a page of their own.</summary>
    private void OpenList(string title, string path, SeerrKind kind) =>
        Navigator.Push(new SeerrListViewModel(Navigator, title, path, kind) { IsPushed = true });

    private static readonly (SeerrRequestFilter Filter, string Title)[] RequestFilters =
    [
        (SeerrRequestFilter.All, "All"), (SeerrRequestFilter.Pending, "Pending"), (SeerrRequestFilter.Approved, "Approved"),
        (SeerrRequestFilter.Processing, "Processing"), (SeerrRequestFilter.Available, "Available"), (SeerrRequestFilter.Unavailable, "Declined"),
    ];

    /// <summary>
    /// Requests as the Apple TV app lists them: the state filters (and
    /// Everyone's or Mine, for whoever manages them), then a row each,
    /// newest first: poster, title, which seasons, the state in its colour,
    /// who asked and when. Three pages, sixty at most.
    /// </summary>
    private async Task<IReadOnlyList<BrowseRow>> RequestRowsAsync(SeerrAccount account)
    {
        var me = await SeerrClient.MeAsync(account);
        var mine = onlyMine || !me.ManagesRequests;
        void Set(SeerrRequestFilter filter, bool ownOnly)
        {
            (requestFilter, onlyMine) = (filter, ownOnly);
            _ = LoadAsync();
        }
        var groups = new List<IReadOnlyList<Choice>>
        {
            RequestFilters.Select(f => new Choice(f.Title, f.Filter == requestFilter, new RelayCommand(() => Set(f.Filter, onlyMine)))).ToList(),
        };
        if (me.ManagesRequests)
            groups.Add([new Choice("Everyone's", !onlyMine, new RelayCommand(() => Set(requestFilter, false))), new Choice("Mine", onlyMine, new RelayCommand(() => Set(requestFilter, true)))]);
        var rows = new List<BrowseRow> { new SegmentRow(groups) };

        var requests = new List<SeerrRequest>();
        for (var page = 1; page <= 3; page++)
        {
            var reply = await SeerrClient.RequestsAsync(requestFilter, mine, account.UserId, page, account);
            requests.AddRange(reply.Results);
            if (page >= (reply.TotalPages ?? 1)) break;
        }
        if (requests.Count == 0)
        {
            rows.Add(new StatusRow("No requests", requestFilter == SeerrRequestFilter.All ? "Films and shows asked for through Seerr show here." : "None in this state."));
            return rows;
        }
        // A show asked for more than once (a season at a time) is looked up once.
        var titles = await SeerrClient.TitlesForAsync(requests.Select(r => (r.Media.TmdbId, r.Media.MediaType ?? r.Type ?? "movie")).Distinct().ToList(), account);
        var byKey = titles.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var request in requests)
        {
            if (!byKey.TryGetValue($"{request.Media.MediaType ?? request.Type ?? "movie"}-{request.Media.TmdbId}", out var title)) continue;
            var seasons = request.Seasons?.Select(s => s.SeasonNumber).Order().ToList() ?? [];
            var meta = string.Join(" · ", new[]
            {
                title.Year, title.IsMovie ? "Film" : "Series",
                seasons.Count == 0 ? null : seasons.Count == 1 ? $"Season {seasons[0]}" : $"Seasons {string.Join(", ", seasons)}",
            }.OfType<string>());
            var byline = string.Join(" · ", new[]
            {
                request.RequestedBy?.DisplayName,
                request.Created?.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture),
            }.OfType<string>());
            var asked = request;
            rows.Add(new RequestRow(SeerrPages.Url(SeerrClient.Image(title.PosterPath, "w342")), title.DisplayTitle, meta, request.StateLabel,
                StateColor(request), byline, new AsyncRelayCommand(() => SeerrPages.OpenAsync(Navigator, title, asked))));
        }
        return rows;
    }

    /// <summary>A request's state in the colours Seerr's own list uses: waiting orange, on its way blue, there green, turned down red.</summary>
    private static Avalonia.Media.IBrush StateColor(SeerrRequest request) => Avalonia.Media.Brush.Parse(request.Status switch
    {
        1 => "#FF9F0A",
        3 or 4 => "#FF453A",
        _ => request.Media.Status switch
        {
            SeerrStatus.Available => "#30D158",
            SeerrStatus.PartiallyAvailable => "#64D2FF",
            SeerrStatus.Processing => "#0A84FF",
            _ => "#E6FFFFFF",
        },
    });

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
            cards = titles.DistinctBy(t => t.Key).Not4k(await SeerrPages.Uhd4kFilmsAsync(Navigator)).Select(t => (object)new SeerrCard(Navigator, t)).ToList();
            return [new TitleRow(title, this)];
        }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException)
        {
            return [new TitleRow(title, this), new StatusRow("Couldn't reach Seerr", error.Message)];
        }
    }
}
