using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tracking;

/// <summary>
/// The server's Aster Discovery and Tracking plugin (aster-companion-plugins/AsterTracking): whether Seerr's features are on and
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

    /// <summary>
    /// The whole Discover page, built on the server for this person: rows in order (Recommended for You, Because You
    /// Watched, On Your Server, watchlists, trending…). The server keeps it warm, so it's usually instant;
    /// <paramref name="fresh"/> builds it again.
    /// </summary>
    public static Task<DiscoverHome> HomeAsync(JellyfinClient jellyfin, bool fresh = false, CancellationToken ct = default) =>
        jellyfin.GetAsync<DiscoverHome>("/AsterTracking/Discover/Home", fresh ? new() { ["fresh"] = "true" } : null, ct);

    /// <summary>
    /// Jellyfin's provider ids ("Tmdb", "Imdb", "Tvdb", "AniDB", "AniList", "MyAnimeList"…) as the plugin names them;
    /// any it doesn't know are left out.
    /// </summary>
    public static Dictionary<string, string> TrackingIds(IReadOnlyDictionary<string, string>? providerIds)
    {
        var ids = new Dictionary<string, string>();
        foreach (var (key, raw) in providerIds ?? new Dictionary<string, string>())
        {
            if (raw?.Trim() is not { Length: > 0 } value) continue;
            var name = key.ToLowerInvariant() switch
            {
                "tmdb" => "tmdb",
                "imdb" => "imdb",
                "tvdb" => "tvdb",
                "anidb" => "anidb",
                "anilist" => "anilist",
                "myanimelist" or "mal" => "mal",
                "simkl" => "simkl",
                _ => null,
            };
            if (name is not null) ids[name] = value;
        }
        return ids;
    }

    /// <summary>
    /// Where a title stands on each watchlist the server can add to, linked or not; empty without the plugin, on one too
    /// old to add to watchlists (<paramref name="features"/> names none), or when the server can't say.
    /// </summary>
    public static async Task<IReadOnlyList<WatchlistState>> WatchlistStateAsync(JellyfinClient jellyfin, TrackingFeatures? features,
        string mediaType, IReadOnlyDictionary<string, string> ids, CancellationToken ct = default)
    {
        if (features is not { Watchlist.Count: > 0 } || ids.Count == 0) return [];
        var query = new Dictionary<string, string?> { ["mediaType"] = mediaType };
        foreach (var (key, value) in ids) if (!string.IsNullOrWhiteSpace(value)) query[key] = value;
        try
        {
            var reply = await jellyfin.GetAsync<WatchlistReply>("/AsterTracking/Watchlist", query, ct).ConfigureAwait(false);
            return reply.Services.Where(s => features.Watchlist.Contains(s.Service)).ToList();
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"Watchlist state: {error.Message}");
            return [];
        }
    }

    /// <summary>Puts a title on <paramref name="service"/>'s watchlist (<paramref name="on"/>) or takes it off; whether it's on it now.</summary>
    /// <exception cref="WatchlistException">With words to show: not linked, signed out, a title it doesn't know, unreachable.</exception>
    public static async Task<bool> SetWatchlistAsync(JellyfinClient jellyfin, string service, bool on, WatchlistTitle title, CancellationToken ct = default)
    {
        var name = new WatchlistState { Service = service }.ServiceName;
        try
        {
            var reply = await jellyfin.SendJsonAsync<WatchlistChange>(on ? HttpMethod.Post : HttpMethod.Delete,
                $"/AsterTracking/Watchlist/{Uri.EscapeDataString(service)}", title, ct: ct).ConfigureAwait(false);
            return reply.On ?? on;
        }
        catch (JellyfinException error) when (error.StatusCode is { } status and >= 400)
        {
            throw new WatchlistException(status switch
            {
                409 => $"Link {name} first, on the Aster Discovery and Tracking plugin's page.",
                410 => $"{name} needs linking again.",
                422 => $"{name} doesn't know this title.",
                502 => $"{name} couldn't be reached. Try again.",
                _ => $"The server said {status}.",
            }, error);
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException or TimeoutException)
        {
            throw new WatchlistException($"The server couldn't be reached: {error.Message}", error);
        }
    }

    private sealed record WatchlistReply
    {
        public List<WatchlistState> Services { get; init; } = [];
    }

    private sealed record WatchlistChange
    {
        public bool? On { get; init; }
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

    /// <summary>The services whose watchlist the apps can add to (<c>/AsterTracking/Watchlist</c>): "simkl"; empty on a plugin before 0.9.8.</summary>
    public List<string> Watchlist { get; init; } = [];
}

/// <summary>Where a title stands on one of the person's watchlists: <see cref="On"/> is null when it couldn't be told.</summary>
public sealed record WatchlistState
{
    public string Service { get; init; } = "";

    public bool Linked { get; init; }

    public bool? On { get; init; }

    /// <summary>"Simkl".</summary>
    public string ServiceName => Service.Length == 0 ? "" : char.ToUpperInvariant(Service[0]) + Service[1..];
}

/// <summary>A title to put on a watchlist or take off it, as the plugin takes one.</summary>
public sealed record WatchlistTitle
{
    /// <summary>"movie" or "tv".</summary>
    public string MediaType { get; init; } = "tv";

    public bool Anime { get; init; }

    /// <summary>"tmdb", "imdb", "tvdb", "mal", "anidb", "anilist", "simkl": any known.</summary>
    public Dictionary<string, string> Ids { get; init; } = [];

    public string? Title { get; init; }

    public int? Year { get; init; }
}

/// <summary>The plugin couldn't change the watchlist; <see cref="Exception.Message"/> says why, in words to show.</summary>
public sealed class WatchlistException(string message, Exception? inner = null) : Exception(message, inner);

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

public sealed record DiscoverHome
{
    public List<DiscoverRow> Rows { get; init; } = [];

    public DateTimeOffset Built { get; init; }
}

public sealed record DiscoverRow
{
    public string Id { get; init; } = "";

    /// <summary>"continue", "recommended", "because", "library", "watchlist", "trending", "season".</summary>
    public string Kind { get; init; } = "";

    public string Title { get; init; } = "";

    public string? Subtitle { get; init; }

    public List<DiscoverItem> Items { get; init; } = [];
}
