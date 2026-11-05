using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tracking;

/// <summary>
/// The server's Aster Tracking plugin (aster-companion-plugins/AsterTracking): whether Seerr's features are on and
/// where Seerr is, and the Discover lists from each person's linked Simkl, AniList and MyAnimeList accounts. A server
/// without the plugin answers 404, which counts as Seerr off and no lists.
/// </summary>
public static class AsterTracking
{
    /// <summary>The server's features, or null without the plugin (or when the server can't be asked).</summary>
    public static async Task<TrackingFeatures?> FeaturesAsync(JellyfinClient jellyfin, CancellationToken ct = default)
    {
        try
        {
            return await jellyfin.GetAsync<TrackingFeatures>("/AsterTracking/Features", ct: ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The lists this person can browse, in the order Discover shows them.</summary>
    public static Task<List<DiscoverList>> ListsAsync(JellyfinClient jellyfin, CancellationToken ct = default) =>
        jellyfin.GetAsync<List<DiscoverList>>("/AsterTracking/Discover", ct: ct);

    /// <summary>A page of one list.</summary>
    public static Task<DiscoverPage> PageAsync(JellyfinClient jellyfin, DiscoverList list, int page = 1, int pageSize = 30, CancellationToken ct = default) =>
        jellyfin.GetAsync<DiscoverPage>($"/AsterTracking/Discover/{Uri.EscapeDataString(list.Service)}/{Uri.EscapeDataString(list.List)}",
            new() { ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture), ["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
}

public sealed record TrackingFeatures
{
    public SeerrFeature Seerr { get; init; } = new();

    /// <summary>The tracking services people can link here: "simkl", "anilist", …</summary>
    public List<string> Services { get; init; } = [];
}

public sealed record SeerrFeature
{
    public bool Enabled { get; init; }

    /// <summary>Seerr's API through the server ("/AsterTracking/Seerr/api/v1"), when it's on.</summary>
    public string? Proxy { get; init; }
}

public sealed record DiscoverList
{
    public string Service { get; init; } = "";

    public string List { get; init; } = "";

    /// <summary>"Watchlist", "Watching", "Recommended", "Trending", "This Season".</summary>
    public string Kind { get; init; } = "";

    public string Title { get; init; } = "";

    public bool Personal { get; init; }
}

public sealed record DiscoverPage
{
    public List<DiscoverItem> Items { get; init; } = [];

    public int Page { get; init; }

    public int TotalItems { get; init; }

    public bool HasMore { get; init; }
}

public sealed record DiscoverItem
{
    public string Title { get; init; } = "";

    public int? Year { get; init; }

    /// <summary>"movie" or "tv".</summary>
    public string MediaType { get; init; } = "tv";

    /// <summary>"tmdb", "tvdb", "imdb", "anilist", "mal", "anidb", "tmdbSeason".</summary>
    public Dictionary<string, string> Ids { get; init; } = [];

    public string? Poster { get; init; }

    public string? Overview { get; init; }

    public string? Progress { get; init; }

    public bool Anime { get; init; }

    public LibraryMatch? InLibrary { get; init; }

    public int? TmdbId => Ids.TryGetValue("tmdb", out var id) && int.TryParse(id, out var tmdb) ? tmdb : null;
}

public sealed record LibraryMatch
{
    public string ItemId { get; init; } = "";

    public string Name { get; init; } = "";

    public string Type { get; init; } = "";
}
