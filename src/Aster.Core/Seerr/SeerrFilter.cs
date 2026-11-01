using System.Globalization;

namespace Aster.Core.Seerr;

/// <summary>How Discover's Movies and Series grids are sorted, as Seerr's own Discover filters name it.</summary>
public enum SeerrSort { Popular, Rating, Newest, Oldest, Title }

/// <summary>
/// How the Movies and Series grids are narrowed and sorted, written as
/// Seerr's own Discover filters write it. The Apple app's SeerrFilter.
/// </summary>
public sealed record SeerrFilter(SeerrKind Kind)
{
    public SeerrSort Sort { get; init; } = SeerrSort.Popular;
    public IReadOnlySet<int> Genres { get; init; } = new HashSet<int>();
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
    /// <summary>Out of ten.</summary>
    public double? MinimumRating { get; init; }
    /// <summary>Minutes.</summary>
    public int? MinimumRuntime { get; init; }
    public int? MaximumRuntime { get; init; }
    /// <summary>ISO 639-1, as TMDB's original language.</summary>
    public string? Language { get; init; }
    /// <summary>A studio (films) or network (shows).</summary>
    public SeerrCompany? Company { get; init; }
    public IReadOnlySet<int> Providers { get; init; } = new HashSet<int>();
    public string Region { get; init; } = SeerrClient.Region;
    /// <summary>Keyword ids, comma-separated, from a custom row.</summary>
    public string? Keywords { get; init; }

    /// <summary>TMDB's Animation genre.</summary>
    public const int AnimationGenre = 16;

    public static string SortLabel(SeerrSort sort) => sort switch
    {
        SeerrSort.Popular => "Most Popular",
        SeerrSort.Rating => "Highest Rated",
        SeerrSort.Newest => "Newest",
        SeerrSort.Oldest => "Oldest",
        _ => "Title",
    };

    private string SortValue()
    {
        var date = Kind == SeerrKind.Movie ? "primary_release_date" : "first_air_date";
        return Sort switch
        {
            SeerrSort.Popular => "popularity.desc",
            SeerrSort.Rating => "vote_average.desc",
            SeerrSort.Newest => $"{date}.desc",
            SeerrSort.Oldest => $"{date}.asc",
            _ => Kind == SeerrKind.Movie ? "title.asc" : "original_name.asc",
        };
    }

    /// <summary>Anime: the Animation genre, originally in Japanese.</summary>
    public bool IsAnime => Genres.Contains(AnimationGenre) && Language == "ja";

    /// <summary>Anime on, or off: off takes both away, leaving any other genres chosen.</summary>
    public SeerrFilter WithAnime(bool on)
    {
        var genres = new HashSet<int>(Genres);
        if (on)
        {
            genres.Add(AnimationGenre);
            return this with { Genres = genres, Language = "ja" };
        }
        genres.Remove(AnimationGenre);
        return this with { Genres = genres, Language = Language == "ja" ? null : Language };
    }

    /// <summary>How many filters are set, for the Filters button.</summary>
    public int ActiveCount => new[]
    {
        Genres.Count > 0, FromYear is not null || ToYear is not null, MinimumRating is not null,
        MinimumRuntime is not null || MaximumRuntime is not null, Language is not null, Company is not null,
        Providers.Count > 0, Keywords is not null,
    }.Count(set => set);

    /// <summary>The <c>/discover/movies</c> or <c>/discover/tv</c> query for <paramref name="page"/>.</summary>
    public string Path(int page)
    {
        var items = new List<(string Name, string Value)> { ("page", page.ToString(CultureInfo.InvariantCulture)), ("sortBy", SortValue()) };
        // Seerr turns away a bare "," (it wants "%2C", which TMDB reads as
        // "and": every genre chosen); "|" ("or", for services) it takes.
        if (Genres.Count > 0) items.Add(("genre", string.Join("%2C", Genres.Order())));
        var (gte, lte) = Kind == SeerrKind.Movie ? ("primaryReleaseDateGte", "primaryReleaseDateLte") : ("firstAirDateGte", "firstAirDateLte");
        if (FromYear is { } from) items.Add((gte, $"{from}-01-01"));
        if (ToYear is { } to) items.Add((lte, $"{to}-12-31"));
        if (MinimumRating is { } rating)
        {
            items.Add(("voteAverageGte", rating.ToString("0.##", CultureInfo.InvariantCulture)));
            // Without a floor of votes, a film three people rated tops the list.
            items.Add(("voteCountGte", "50"));
        }
        if (MinimumRuntime is { } shortest) items.Add(("withRuntimeGte", shortest.ToString(CultureInfo.InvariantCulture)));
        if (MaximumRuntime is { } longest) items.Add(("withRuntimeLte", longest.ToString(CultureInfo.InvariantCulture)));
        if (Language is { } language) items.Add(("language", Uri.EscapeDataString(language)));
        if (Company is { } company) items.Add((Kind == SeerrKind.Movie ? "studio" : "network", company.Id.ToString(CultureInfo.InvariantCulture)));
        if (Providers.Count > 0)
        {
            items.Add(("watchProviders", string.Join("|", Providers.Order())));
            items.Add(("watchRegion", Region));
        }
        if (Keywords is { } keywords) items.Add(("keywords", keywords.Replace(",", "%2C", StringComparison.Ordinal)));
        return $"/discover/{(Kind == SeerrKind.Movie ? "movies" : "tv")}?{string.Join("&", items.Select(i => $"{i.Name}={i.Value}"))}";
    }
}
