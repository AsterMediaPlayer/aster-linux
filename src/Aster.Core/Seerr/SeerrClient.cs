using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aster.Core.Seerr;

/// <summary>
/// Seerr's API, signed in to the way its own site does (a Jellyfin or Emby
/// user and password, Jellyfin Quick Connect, the Plex account's token, or a
/// Seerr account), which leaves a session cookie: that cookie is all that's
/// kept, sent by hand so it can sync to the viewer's other devices.
/// </summary>
public static class SeerrClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A TMDB picture at a width ("w342", "w1280"), or null with no path.</summary>
    public static string? Image(string? path, string width) => string.IsNullOrEmpty(path) ? null : $"https://image.tmdb.org/t/p/{width}{path}";

    /// <summary>A company's logo, white on the dark page.</summary>
    public static string? LogoUrl(string? path) => string.IsNullOrEmpty(path) ? null : $"https://image.tmdb.org/t/p/w500_filter(duotone,ffffff,bababa){path}";

    /// <summary>"192.168.1.240:5055" or "https://requests.example.com/" as an address.</summary>
    public static Uri? Address(string text)
    {
        var trimmed = text.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Host.Length > 0 ? url : null;
    }

    private static string Base(Uri url) => url.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>
    /// Where this computer reaches an address: as it is, or for a one-word
    /// name ("server:5055") that a phone finds on the local network but Linux
    /// knows by mDNS as "server.local", that. Both are looked up at once: a
    /// name DNS doesn't know takes seconds to fail, and mDNS answers at once.
    /// </summary>
    public static async Task<Uri> ReachableAsync(Uri url)
    {
        var host = url.Host;
        if (host.Contains('.', StringComparison.Ordinal) || IPAddress.TryParse(host, out _)) return url;
        var bare = ResolvesAsync(host);
        var local = ResolvesAsync(host + ".local");
        bool useLocal;
        if (await Task.WhenAny(bare, local).ConfigureAwait(false) == local && await local.ConfigureAwait(false)) useLocal = true;
        else if (await bare.ConfigureAwait(false)) useLocal = false;
        else useLocal = await local.ConfigureAwait(false);
        return useLocal ? new UriBuilder(url) { Host = host + ".local" }.Uri : url;
    }

    private static async Task<bool> ResolvesAsync(string host)
    {
        try { return (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false)).Length > 0; }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    public static async Task<(byte[] Body, HttpResponseMessage Response)> SendAsync(Uri baseUrl, string path, HttpMethod? method = null,
        object? body = null, string? cookie = null, CancellationToken ct = default, string? authorization = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, Base(baseUrl) + "/api/v1" + path);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        // Through the server's plugin: the Jellyfin sign-in, in place of a Seerr session.
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (bytes, response);
    }

    public static async Task<T> GetAsync<T>(SeerrAccount account, string path, CancellationToken ct = default)
    {
        var (data, response) = await SendAsync(account.Url, path, cookie: account.Cookie, ct: ct, authorization: account.Authorization).ConfigureAwait(false);
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 401) throw new SeerrException(SeerrFailure.SignedOut);
            if (status == 403)
            {
                // Seerr says 403 both to no session and to a user who may not
                // see this; only the first is being signed out.
                var (_, me) = await SendAsync(account.Url, "/auth/me", cookie: account.Cookie, ct: ct, authorization: account.Authorization).ConfigureAwait(false);
                using (me)
                {
                    throw me.StatusCode == HttpStatusCode.OK ? new SeerrException(SeerrFailure.Http, status: 403) : new SeerrException(SeerrFailure.SignedOut);
                }
            }
            if (status != 200) throw new SeerrException(SeerrFailure.Http, status: status);
            return JsonSerializer.Deserialize<T>(data, Json) ?? throw new SeerrException(SeerrFailure.Http, "Seerr's answer couldn't be read.", status);
        }
    }

    private static string? Message(byte[] data)
    {
        try { return JsonNode.Parse(data)?["message"]?.GetValue<string>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    // ---- Signing in ----

    /// <summary>How a Seerr server lets people sign in.</summary>
    public sealed record SignOptions
    {
        /// <summary>1 Plex, 2 Jellyfin, 3 Emby, 4 none.</summary>
        public int? MediaServerType { get; init; }
        public bool? LocalLogin { get; init; }
        public bool? MediaServerLogin { get; init; }
        public string? ApplicationTitle { get; init; }
        /// <summary>Whether a 4K Radarr (films) or Sonarr (shows) is set up.</summary>
        public bool? Movie4kEnabled { get; init; }
        public bool? Series4kEnabled { get; init; }

        public bool SignsInWithPlex => MediaServerType == 1 && MediaServerLogin != false;
        public bool SignsInWithJellyfinOrEmby => MediaServerType is 2 or 3 && MediaServerLogin != false;
    }

    public static async Task<SignOptions> SignInOptionsAsync(Uri baseUrl, CancellationToken ct = default)
    {
        var (data, response) = await SendAsync(baseUrl, "/settings/public", ct: ct).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new SeerrException(SeerrFailure.Http, status: (int)response.StatusCode);
            return JsonSerializer.Deserialize<SignOptions>(data, Json) ?? new();
        }
    }

    /// <summary>With the media server's user and password (Jellyfin, Emby).</summary>
    public static Task<SeerrAccount> SignInAsync(Uri baseUrl, string username, string password, CancellationToken ct = default) =>
        SignInAsync(baseUrl, "/auth/jellyfin", new { username, password }, ct);

    /// <summary>With the Plex account's own token, nothing to type.</summary>
    public static Task<SeerrAccount> SignInWithPlexAsync(Uri baseUrl, string plexToken, CancellationToken ct = default) =>
        SignInAsync(baseUrl, "/auth/plex", new { authToken = plexToken }, ct);

    /// <summary>With a Seerr account of its own (an email address and password).</summary>
    public static Task<SeerrAccount> SignInLocalAsync(Uri baseUrl, string email, string password, CancellationToken ct = default) =>
        SignInAsync(baseUrl, "/auth/local", new { email, password }, ct);

    /// <summary>
    /// Quick Connect through Seerr's Jellyfin: a code to approve there (or
    /// for this app to approve itself, being signed in to it), and a secret
    /// to ask after it with.
    /// </summary>
    public static async Task<(string Code, string Secret)> QuickConnectStartAsync(Uri baseUrl, CancellationToken ct = default)
    {
        var (data, response) = await SendAsync(baseUrl, "/auth/jellyfin/quickconnect/initiate", HttpMethod.Post, ct: ct).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new SeerrException(SeerrFailure.Http, Message(data), (int)response.StatusCode);
            var reply = JsonNode.Parse(data)!;
            return (reply["code"]!.GetValue<string>(), reply["secret"]!.GetValue<string>());
        }
    }

    /// <summary>Whether the code's been approved yet.</summary>
    public static async Task<bool> QuickConnectApprovedAsync(Uri baseUrl, string secret, CancellationToken ct = default)
    {
        var (data, response) = await SendAsync(baseUrl, "/auth/jellyfin/quickconnect/check?secret=" + Uri.EscapeDataString(secret), ct: ct).ConfigureAwait(false);
        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new SeerrException(SeerrFailure.Http, status: (int)response.StatusCode);
            return JsonNode.Parse(data)?["authenticated"]?.GetValue<bool>() == true;
        }
    }

    /// <summary>Signed in once the code's approved.</summary>
    public static Task<SeerrAccount> SignInWithQuickConnectAsync(Uri baseUrl, string secret, CancellationToken ct = default) =>
        SignInAsync(baseUrl, "/auth/jellyfin/quickconnect/authenticate", new { secret }, ct);

    private static async Task<SeerrAccount> SignInAsync(Uri baseUrl, string path, object body, CancellationToken ct)
    {
        var (data, response) = await SendAsync(baseUrl, path, HttpMethod.Post, body, ct: ct).ConfigureAwait(false);
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status != 200)
                throw new SeerrException(SeerrFailure.Refused, Message(data) ?? (status is 401 or 403 ? "That user name or password wasn't right." : null), status);
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var set) ? set.Select(c => c.Split(';')[0].Trim()).ToList() : [];
            var cookie = cookies.FirstOrDefault(c => c.StartsWith("connect.sid=", StringComparison.Ordinal)) ?? cookies.FirstOrDefault()
                ?? throw new SeerrException(SeerrFailure.Http, "Seerr gave no session.", status);
            var user = JsonNode.Parse(data)!;
            string? Text(string key) => user[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
            var name = Text("displayName") ?? Text("jellyfinUsername") ?? Text("plexUsername") ?? Text("username") ?? Text("email") ?? "Seerr";
            return new SeerrAccount(new Uri(Base(baseUrl)), cookie, name, user["id"]!.GetValue<int>());
        }
    }

    // ---- Finding and asking ----

    public static async Task<IReadOnlyList<SeerrTitle>> SearchAsync(string query, SeerrAccount account, CancellationToken ct = default)
    {
        var page = await GetAsync<SeerrPage<SeerrTitle>>(account, $"/search?query={Uri.EscapeDataString(query)}&page=1", ct).ConfigureAwait(false);
        return page.Results.Where(t => t.MediaType is "movie" or "tv").ToList();
    }

    /// <summary>A film's or show's page: overview, trailer, genres, a show's seasons, and where it stands.</summary>
    public static Task<SeerrDetails> DetailsAsync(SeerrTitle title, SeerrAccount account, CancellationToken ct = default) =>
        GetAsync<SeerrDetails>(account, title.IsMovie ? $"/movie/{title.Id}" : $"/tv/{title.Id}", ct);

    public static Task<SeerrShow> ShowAsync(int id, SeerrAccount account, CancellationToken ct = default) =>
        GetAsync<SeerrShow>(account, $"/tv/{id}", ct);

    /// <summary>Asks for a film, or for these seasons of a show; an admin's options say which Sonarr or Radarr, folder and quality.</summary>
    public static async Task RequestAsync(SeerrTitle title, IReadOnlyList<int>? seasons, SeerrAccount account, IDictionary<string, object>? options = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["mediaType"] = title.IsMovie ? "movie" : "tv", ["mediaId"] = title.Id };
        if (!title.IsMovie) body["seasons"] = seasons ?? [];
        foreach (var (key, value) in options ?? new Dictionary<string, object>()) body[key] = value;
        await ActAsync("/request", HttpMethod.Post, account, body, ct).ConfigureAwait(false);
    }

    // ---- Discover's rows ----

    /// <summary>The Discover rows, enabled ones only, in the admin's order.</summary>
    public static async Task<IReadOnlyList<SeerrSlider>> SlidersAsync(SeerrAccount account, CancellationToken ct = default) =>
        (await GetAsync<List<SeerrSlider>>(account, "/settings/discover", ct).ConfigureAwait(false)).Where(s => s.Enabled != false).ToList();

    public static Task<SeerrUser> MeAsync(SeerrAccount account, CancellationToken ct = default) => GetAsync<SeerrUser>(account, "/auth/me", ct);

    /// <summary>Discover's own row, not one of Seerr's: films in cinemas now.</summary>
    public const int InTheatersType = 1000;

    /// <summary>A row's name: the admin's, else Seerr's own for a built-in row.</summary>
    public static string SliderTitle(SeerrSlider slider) => !string.IsNullOrEmpty(slider.Title) ? slider.Title : slider.Type switch
    {
        1 => "Recently Added",
        2 => "Recent Requests",
        3 => "Watchlist",
        4 => "Trending",
        5 => "Popular Movies",
        6 => "Movie Genres",
        7 => "Upcoming Movies",
        8 => "Studios",
        9 => "Popular Series",
        10 => "Series Genres",
        11 => "Upcoming Series",
        12 => "Networks",
        InTheatersType => "In Theaters",
        _ => "More to Discover",
    };

    /// <summary>The titles in a row of titles; null for a row of genres or companies.</summary>
    public static (string Path, SeerrKind? Kind)? TitlesPath(SeerrSlider slider)
    {
        // A list of ids ("28,12") needs its commas encoded for Seerr.
        var data = (slider.Data ?? "").Replace(",", "%2C", StringComparison.Ordinal);
        switch (slider.Type)
        {
            case 3: return ("/discover/watchlist?page=1", null);
            case 4: return ("/discover/trending?page=1", null);
            case 5: return ("/discover/movies?page=1", SeerrKind.Movie);
            case 7: return ("/discover/movies/upcoming?page=1", SeerrKind.Movie);
            case 9: return ("/discover/tv?page=1", SeerrKind.Tv);
            case 11: return ("/discover/tv/upcoming?page=1", SeerrKind.Tv);
            case 13: return ($"/discover/movies?page=1&keywords={data}", SeerrKind.Movie);
            case 14: return ($"/discover/movies?page=1&genre={data}", SeerrKind.Movie);
            case 15: return ($"/discover/tv?page=1&keywords={data}", SeerrKind.Tv);
            case 16: return ($"/discover/tv?page=1&genre={data}", SeerrKind.Tv);
            case 17: return ($"/search?query={Uri.EscapeDataString(slider.Data ?? "")}&page=1", null);
            case 18: return ($"/discover/movies/studio/{data}?page=1", SeerrKind.Movie);
            case 19: return ($"/discover/tv/network/{data}?page=1", SeerrKind.Tv);
            case 20 or 21:
                // "US,8|337": the region, then the services.
                var parts = (slider.Data ?? "").Split(',', 2);
                var region = parts.Length == 2 ? parts[0] : "US";
                var services = parts[^1];
                return slider.Type == 20
                    ? ($"/discover/movies?page=1&watchRegion={region}&watchProviders={services}", SeerrKind.Movie)
                    : ($"/discover/tv?page=1&watchRegion={region}&watchProviders={services}", SeerrKind.Tv);
            default: return null;
        }
    }

    private static string Kind(SeerrKind kind) => kind == SeerrKind.Movie ? "movie" : "tv";

    /// <summary>A page of titles from any of Discover's lists, people left out, each knowing whether it's a film or a show.</summary>
    public static async Task<SeerrPage<SeerrTitle>> TitlesAsync(string path, SeerrKind? kind, SeerrAccount account, CancellationToken ct = default)
    {
        var page = await GetAsync<SeerrPage<SeerrTitle>>(account, path, ct).ConfigureAwait(false);
        return page with
        {
            Results = page.Results.Select(t => kind is { } known ? t with { MediaType = Kind(known) } : t).Where(t => t.MediaType is "movie" or "tv").ToList(),
        };
    }

    /// <summary>
    /// Films in cinemas now, as near as Seerr's filters come (it passes on no
    /// theatrical-release filter): out in the last six weeks with some votes,
    /// most popular first, less those already on the region's big streaming services.
    /// </summary>
    public static async Task<IReadOnlyList<SeerrTitle>> InTheatersAsync(SeerrAccount account, string? region = null, DateTime? now = null, CancellationToken ct = default)
    {
        region ??= Region;
        var (from, to) = TheatricalWindow(now ?? DateTime.Now);
        var recent = $"/discover/movies?sortBy=popularity.desc&primaryReleaseDateGte={from}&primaryReleaseDateLte={to}&voteCountGte=20";
        var services = (await ProvidersAsync(SeerrKind.Movie, region, account, ct).ConfigureAwait(false)).Take(12).Select(p => p.Id.ToString()).ToList();
        var shown = PagesAsync(recent, 3, account, ct);
        var streaming = new HashSet<int>();
        if (services.Count > 0)
            streaming = (await PagesAsync(recent + $"&watchRegion={region}&watchProviders={string.Join("%7C", services)}", 5, account, ct).ConfigureAwait(false)).Select(t => t.Id).ToHashSet();
        return (await shown.ConfigureAwait(false)).Where(t => !streaming.Contains(t.Id)).ToList();
    }

    /// <summary>"2026-09-20" to "2026-11-01": the last six weeks.</summary>
    public static (string From, string To) TheatricalWindow(DateTime now) => (Day(now.AddDays(-42)), Day(now));

    /// <summary>"2026-10-01" to "2027-04-01": the first of last month, and six months on.</summary>
    public static (string From, string To) UpcomingWindow(DateTime now)
    {
        var start = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
        return (Day(start), Day(start.AddMonths(6)));
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The region for streaming services and release dates: the system's, else the US.</summary>
    public static string Region
    {
        get
        {
            try
            {
                var name = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return name.Length == 2 && name != "IV" ? name : "US";
            }
            catch (ArgumentException) { return "US"; }
        }
    }

    private static async Task<IReadOnlyList<SeerrTitle>> PagesAsync(string path, int count, SeerrAccount account, CancellationToken ct)
    {
        var found = new List<SeerrTitle>();
        for (var page = 1; page <= count; page++)
        {
            var reply = await TitlesAsync(path + $"&page={page}", SeerrKind.Movie, account, ct).ConfigureAwait(false);
            found.AddRange(reply.Results);
            if (page >= (reply.TotalPages ?? 1)) break;
        }
        return found;
    }

    /// <summary>
    /// Anime starting or airing around now: Japanese animation first airing
    /// from the first of last month to six months on, newest first, as
    /// Seerr's own Discover filters write it.
    /// </summary>
    public static async Task<IReadOnlyList<SeerrTitle>> UpcomingAnimeAsync(SeerrAccount account, DateTime? now = null, CancellationToken ct = default)
    {
        var (from, to) = UpcomingWindow(now ?? DateTime.Now);
        var found = new List<SeerrTitle>();
        // Two pages at most: forty titles is plenty for a row.
        for (var page = 1; page <= 2; page++)
        {
            var reply = await TitlesAsync($"/discover/tv?language=server%7Cja&sortBy=first_air_date.desc&firstAirDateGte={from}&firstAirDateLte={to}&genre=16&page={page}",
                SeerrKind.Tv, account, ct).ConfigureAwait(false);
            found.AddRange(reply.Results);
            if (page >= (reply.TotalPages ?? 1)) break;
        }
        return found;
    }

    /// <summary>What arrived on the server lately, newest first, as titles.</summary>
    public static async Task<IReadOnlyList<SeerrTitle>> RecentlyAddedAsync(SeerrAccount account, CancellationToken ct = default)
    {
        var media = await GetAsync<SeerrPage<SeerrRequestMedia>>(account, "/media?filter=allavailable&take=20&skip=0&sort=mediaAdded", ct).ConfigureAwait(false);
        return await TitlesForAsync(media.Results.Select(m => (m.TmdbId, m.MediaType ?? "movie")).ToList(), account, ct).ConfigureAwait(false);
    }

    /// <summary>Each (TMDB id, kind) as a title, asked side by side, in order; those that fail are left out.</summary>
    public static async Task<IReadOnlyList<SeerrTitle>> TitlesForAsync(IReadOnlyList<(int Id, string Kind)> ids, SeerrAccount account, CancellationToken ct = default)
    {
        var found = await Task.WhenAll(ids.Select(async pair =>
        {
            try
            {
                var title = await GetAsync<SeerrTitle>(account, pair.Kind == "movie" ? $"/movie/{pair.Id}" : $"/tv/{pair.Id}", ct).ConfigureAwait(false);
                return title with { MediaType = pair.Kind };
            }
            catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or JsonException) { return null; }
        })).ConfigureAwait(false);
        return found.OfType<SeerrTitle>().ToList();
    }

    public static Task<List<SeerrGenre>> GenresAsync(SeerrKind kind, SeerrAccount account, CancellationToken ct = default) =>
        GetAsync<List<SeerrGenre>>(account, $"/discover/genreslider/{Kind(kind)}", ct);

    /// <summary>Seerr's own studios and networks, as its Discover page shows them.</summary>
    public static readonly int[] StudioIds = [2, 127928, 34, 174, 33, 4, 3, 521, 420, 9993, 41077];
    public static readonly int[] NetworkIds = [213, 2739, 1024, 2552, 453, 49, 4353, 2, 19, 6, 16, 56, 1112, 4330];

    /// <summary>Studios (films) or networks (shows), with their logos.</summary>
    public static async Task<IReadOnlyList<SeerrCompany>> CompaniesAsync(SeerrKind kind, SeerrAccount account, CancellationToken ct = default)
    {
        var (ids, route) = kind == SeerrKind.Movie ? (StudioIds, "studio") : (NetworkIds, "network");
        var found = await Task.WhenAll(ids.Select(async id =>
        {
            try { return await GetAsync<SeerrCompany>(account, $"/{route}/{id}", ct).ConfigureAwait(false); }
            catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or JsonException) { return null; }
        })).ConfigureAwait(false);
        return found.OfType<SeerrCompany>().ToList();
    }

    public static async Task<IReadOnlyList<SeerrProvider>> ProvidersAsync(SeerrKind kind, string region, SeerrAccount account, CancellationToken ct = default) =>
        (await GetAsync<List<SeerrProvider>>(account, $"/watchproviders/{(kind == SeerrKind.Movie ? "movies" : "tv")}?watchRegion={region}", ct).ConfigureAwait(false))
            .OrderBy(p => p.DisplayPriority ?? 999).ToList();

    // ---- Requested films on the way ----

    private sealed record Dated(SeerrReleases? Releases);

    /// <summary>The viewer's approved film requests not out yet, soonest first, each with when and how it arrives.</summary>
    public static async Task<IReadOnlyList<SeerrComingFilm>> ComingFilmsAsync(SeerrAccount account, string? region = null, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        region ??= Region;
        var at = now ?? DateTimeOffset.Now;
        var requests = (await RequestsAsync(SeerrRequestFilter.Approved, mine: true, account.UserId, 1, account, ct).ConfigureAwait(false)).Results
            .Where(r => r.Type == "movie" && r.Media.Status != SeerrStatus.Available).ToList();
        var films = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                var path = $"/movie/{request.Media.TmdbId}";
                var title = (await GetAsync<SeerrTitle>(account, path, ct).ConfigureAwait(false)) with { MediaType = "movie" };
                var dated = await GetAsync<Dated>(account, path, ct).ConfigureAwait(false);
                return dated.Releases?.Arrival(region, at) is { } arrival ? new SeerrComingFilm(title, arrival.Date, arrival.How) : null;
            }
            catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or JsonException) { return null; }
        })).ConfigureAwait(false);
        return films.OfType<SeerrComingFilm>().OrderBy(f => f.Date).ToList();
    }

    // ---- Requests ----

    /// <summary>The Sonarr (shows) or Radarr (films) servers, 4K ones too (a request to one asks for the 4K copy).</summary>
    public static async Task<IReadOnlyList<SeerrService>> ServicesAsync(SeerrKind kind, SeerrAccount account, CancellationToken ct = default) =>
        await GetAsync<List<SeerrService>>(account, kind == SeerrKind.Tv ? "/service/sonarr" : "/service/radarr", ct).ConfigureAwait(false);

    public static Task<SeerrServiceChoices> ServiceChoicesAsync(SeerrKind kind, int server, SeerrAccount account, CancellationToken ct = default) =>
        GetAsync<SeerrServiceChoices>(account, kind == SeerrKind.Tv ? $"/service/sonarr/{server}" : $"/service/radarr/{server}", ct);

    private sealed record RequestsReply(RequestsPageInfo PageInfo, List<SeerrRequest> Results);
    private sealed record RequestsPageInfo(int? Pages, int? Page, int? Results);

    /// <summary>A page of requests: everyone's for someone who manages them, else the viewer's own.</summary>
    public static async Task<SeerrPage<SeerrRequest>> RequestsAsync(SeerrRequestFilter filter, bool mine, int userId, int page, SeerrAccount account, CancellationToken ct = default)
    {
        const int take = 20;
        var path = $"/request?take={take}&skip={(page - 1) * take}&filter={filter.ToString().ToLowerInvariant()}&sort=added";
        if (mine) path += $"&requestedBy={userId}";
        var reply = await GetAsync<RequestsReply>(account, path, ct).ConfigureAwait(false);
        return new SeerrPage<SeerrRequest> { Page = reply.PageInfo.Page, TotalPages = reply.PageInfo.Pages, TotalResults = reply.PageInfo.Results, Results = reply.Results };
    }

    public static Task ApproveAsync(SeerrRequest request, SeerrAccount account, CancellationToken ct = default) =>
        ActAsync($"/request/{request.Id}/approve", HttpMethod.Post, account, null, ct);

    public static Task DeclineAsync(SeerrRequest request, SeerrAccount account, CancellationToken ct = default) =>
        ActAsync($"/request/{request.Id}/decline", HttpMethod.Post, account, null, ct);

    /// <summary>Withdraws a request not yet approved.</summary>
    public static Task CancelAsync(SeerrRequest request, SeerrAccount account, CancellationToken ct = default) =>
        ActAsync($"/request/{request.Id}", HttpMethod.Delete, account, null, ct);

    private static async Task ActAsync(string path, HttpMethod method, SeerrAccount account, object? body, CancellationToken ct)
    {
        var (data, response) = await SendAsync(account.Url, path, method, body, account.Cookie, ct, account.Authorization).ConfigureAwait(false);
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 401) throw new SeerrException(SeerrFailure.SignedOut);
            if (status is < 200 or >= 300) throw new SeerrException(SeerrFailure.Refused, Message(data), status);
        }
    }
}
