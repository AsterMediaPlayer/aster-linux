using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Ratings;

public enum ScoreSource
{
    Tomatometer,
    Popcorn,
    Imdb,
    Tmdb,
    Metacritic,
    Letterboxd,

    /// <summary>The server's own score, unattributed.</summary>
    Community,
}

/// <summary>
/// One site's score for a title, on a 0 to 10 scale, written as that site
/// writes it: IMDb 7.8, Rotten Tomatoes' Tomatometer and audience score as
/// percentages (fresh at 60%), the Metascore out of 100, Letterboxd out of
/// five, TMDB as a percentage. The Apple app's TitleScore.
/// </summary>
public sealed record TitleScore(ScoreSource Source, double Value)
{
    /// <summary>A score on the site's own scale; missing, zero (sites write it for "not rated yet") or out of range isn't one.</summary>
    public static TitleScore? Of(ScoreSource source, double? value, double outOf) =>
        value is { } v && double.IsFinite(v) && v > 0 && v <= outOf ? new TitleScore(source, v * 10 / outOf) : null;

    /// <summary>As the site writes it: "7.8", "92%", "74", "3.9".</summary>
    public string Text => Source switch
    {
        ScoreSource.Imdb or ScoreSource.Community => Value.ToString("0.0", CultureInfo.InvariantCulture),
        ScoreSource.Tomatometer or ScoreSource.Popcorn or ScoreSource.Tmdb => $"{Math.Round(Value * 10, MidpointRounding.AwayFromZero):0}%",
        ScoreSource.Metacritic => $"{Math.Round(Value * 10, MidpointRounding.AwayFromZero):0}",
        _ => (Value / 2).ToString("0.0", CultureInfo.InvariantCulture),
    };

    public string SiteName => Source switch
    {
        ScoreSource.Tomatometer => "Rotten Tomatoes",
        ScoreSource.Popcorn => "Rotten Tomatoes audience score",
        ScoreSource.Imdb => "IMDb",
        ScoreSource.Tmdb => "TMDB",
        ScoreSource.Metacritic => "Metacritic",
        ScoreSource.Letterboxd => "Letterboxd",
        _ => "Rating",
    };

    /// <summary>The Tomatometer's own line between fresh and rotten.</summary>
    public bool IsFresh => Value >= 6;

    public static IReadOnlyList<TitleScore> Ordered(IEnumerable<TitleScore> scores) => scores.OrderBy(s => s.Source).ToList();

    /// <summary>
    /// The server's scores with MDBList's over them: a site's score from
    /// MDBList replaces the server's copy, and the server's unattributed one
    /// goes once there's a real one.
    /// </summary>
    public static IReadOnlyList<TitleScore> Merged(IReadOnlyList<TitleScore> own, IReadOnlyList<TitleScore> added)
    {
        if (added.Count == 0) return Ordered(own);
        var covered = added.Select(s => s.Source).ToHashSet();
        return Ordered(added.Concat(own.Where(s => !covered.Contains(s.Source) && s.Source != ScoreSource.Community)));
    }

    /// <summary>The scores the server sent with the title: Jellyfin's and Emby's critic score is the Tomatometer, their community score unattributed.</summary>
    public static IReadOnlyList<TitleScore> FromServer(BaseItem item) =>
        new[] { Of(ScoreSource.Tomatometer, item.CriticRating, 100), Of(ScoreSource.Community, item.CommunityRating, 10) }.OfType<TitleScore>().ToList();
}

/// <summary>
/// Scores for a film or show from MDBList (mdblist.com), which gathers them
/// from IMDb, Rotten Tomatoes, Metacritic, Letterboxd and TMDB daily. Needs
/// the viewer's own free API key, kept with the sign-ins (the Secret
/// Service); asked once per title for twelve hours. The Apple app's MDBList.
/// </summary>
public static class MdbList
{
    private const string Account = "mdblistKey";
    private static readonly TimeSpan Life = TimeSpan.FromHours(12);
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<TitleScore> Scores, DateTimeOffset At)> Kept = new();

    /// <summary>Its own client: nothing of the server's goes to MDBList.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>The key; null when none's been given. Setting it (empty to clear) forgets the scores asked for with the last.</summary>
    public static string? ApiKey
    {
        // Secrets, not CredentialStore's own calls: those are Windows' Credential Manager alone.
        get => Secrets.Read(Account) is { Length: > 0 } key ? key : null;
        set
        {
            var trimmed = value?.Trim() ?? "";
            if (trimmed.Length == 0) Secrets.Delete(Account);
            else Secrets.Write(Account, trimmed);
            Kept.Clear();
        }
    }

    /// <summary>The title's scores, by its TMDB, IMDb or TVDB id; none without a key, for anything but a film or show, or when MDBList doesn't know it.</summary>
    public static async Task<IReadOnlyList<TitleScore>> ScoresAsync(BaseItem item, CancellationToken ct = default)
    {
        if (ApiKey is not { } key || Lookup(item) is not var (provider, type, id)) return [];
        var cacheKey = $"{provider}/{type}/{id}";
        if (Kept.TryGetValue(cacheKey, out var kept) && DateTimeOffset.UtcNow - kept.At < Life) return kept.Scores;
        var url = $"https://api.mdblist.com/{provider}/{type}/{Uri.EscapeDataString(id)}?apikey={Uri.EscapeDataString(key)}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            // A failure isn't kept, so the next visit asks again; "not known" is.
            if (response.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NotFound)) return [];
            var scores = response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) : [];
            Kept[cacheKey] = (scores, DateTimeOffset.UtcNow);
            return scores;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { return []; }
    }

    /// <summary>Which id MDBList is asked by: TMDB first, then IMDb, then TVDB (shows only).</summary>
    public static (string Provider, string Type, string Id)? Lookup(BaseItem item)
    {
        var type = item.Kind switch { ItemKind.Movie => "movie", ItemKind.Series => "show", _ => null };
        if (type is null) return null;
        string? Id(string name) => item.ProviderIds?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v : null;
        if (Id("Tmdb") is { } tmdb) return ("tmdb", type, tmdb);
        if (Id("Imdb") is { } imdb) return ("imdb", type, imdb);
        if (Id("Tvdb") is { } tvdb && type == "show") return ("tvdb", type, tvdb);
        return null;
    }

    /// <summary>The <c>ratings</c> of a reply, each <c>value</c> on its site's own scale.</summary>
    public static IReadOnlyList<TitleScore> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Array) return [];
            var scores = new List<TitleScore>();
            foreach (var rating in ratings.EnumerateArray())
            {
                var source = rating.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.ToLowerInvariant() : "";
                double? value = rating.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                var score = source switch
                {
                    "imdb" => TitleScore.Of(ScoreSource.Imdb, value, 10),
                    "tomatoes" => TitleScore.Of(ScoreSource.Tomatometer, value, 100),
                    "popcorn" or "tomatoesaudience" => TitleScore.Of(ScoreSource.Popcorn, value, 100),
                    "metacritic" => TitleScore.Of(ScoreSource.Metacritic, value, 100),
                    "letterboxd" => TitleScore.Of(ScoreSource.Letterboxd, value, 5),
                    "tmdb" => TitleScore.Of(ScoreSource.Tmdb, value, 100),
                    _ => null,
                };
                if (score is not null) scores.Add(score);
            }
            return TitleScore.Ordered(scores);
        }
        catch (JsonException) { return []; }
    }
}
