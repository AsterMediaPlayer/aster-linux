using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Core.Seerr;

/// <summary>
/// Seerr's replies as its 3.5 server gives them (trimmed from a real one, as
/// the Apple app's SeerrTests has them): how sign-in is offered, a search, a
/// show's seasons, Discover's rows; and the accounts' scrambling, which must
/// open what the Apple and Android apps seal.
/// </summary>
public class SeerrTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, SeerrClient.Json)!;

    [Fact]
    public void The4kCopyStandsApart()
    {
        // The film's here; its 4K copy isn't asked for. A show's seasons likewise.
        var info = Read<SeerrMediaInfo>("""{"status": 5, "status4k": 1, "seasons": [{"seasonNumber": 1, "status": 5, "status4k": 2}]}""");
        Assert.Equal(SeerrStatus.Available, info.Status);
        var uhd = info.For4k();
        Assert.Equal(info.Status4k, uhd.Status);
        Assert.True(uhd.Status!.Value.CanRequest());
        Assert.Equal(info.Seasons![0].Status4k, uhd.Seasons![0].Status);
    }

    [Theory]
    [InlineData(2, true, true)]        // admin
    [InlineData(1024, false, true)]    // REQUEST_4K
    [InlineData(2048, true, true)]     // REQUEST_4K_MOVIE, a film
    [InlineData(2048, false, false)]   // ... not a show
    [InlineData(4096, false, true)]    // REQUEST_4K_TV, a show
    [InlineData(32, true, false)]      // REQUEST only
    public void Who_may_ask_for_4k(int permissions, bool movie, bool may) =>
        Assert.Equal(may, new SeerrUser { Permissions = permissions }.Requests4k(movie));

    [Fact]
    public void Makers_are_directors_else_creators()
    {
        var film = Read<SeerrDetails>("""{"credits": {"cast": [{"id": 1, "name": "Chris Sarandon", "character": "Jack", "profilePath": "/a.jpg"}], "crew": [{"name": "Henry Selick", "job": "Director"}, {"name": "Danny Elfman", "job": "Original Music Composer"}]}, "releaseDate": "1993-10-09", "productionCompanies": [{"id": 2, "name": "Touchstone Pictures"}]}""");
        Assert.Equal(["Henry Selick"], film.Makers);
        Assert.Equal(new DateTime(1993, 10, 9), film.Date);
        Assert.Equal("Jack", film.Credits!.Cast![0].Character);
        var show = Read<SeerrDetails>("""{"createdBy": [{"id": 3, "name": "Vince Gilligan"}], "numberOfSeasons": 5}""");
        Assert.Equal(["Vince Gilligan"], show.Makers);
    }

    [Fact]
    public void SignInIsOffered()
    {
        var sign = Read<SeerrClient.SignOptions>("""{"mediaServerType": 2, "localLogin": true, "mediaServerLogin": true, "applicationTitle": "Seerr"}""");
        Assert.True(sign.SignsInWithJellyfinOrEmby);
        Assert.False(sign.SignsInWithPlex);
    }

    [Fact]
    public void SearchResultsAreRead()
    {
        var page = Read<SeerrPage<SeerrTitle>>("""{"page": 1, "results": [{"id": 438631, "mediaType": "movie", "title": "Dune", "releaseDate": "2021-09-15", "posterPath": "/v1tRXZ4JtD2Iv6fjkPvT4GiwslV.jpg"}, {"id": 90228, "mediaType": "tv", "name": "Dune: Prophecy", "firstAirDate": "2024-11-17"}]}""");
        var dune = page.Results[0];
        Assert.Equal(438631, dune.Id);
        Assert.True(dune.IsMovie);
        Assert.Equal("2021", dune.Year);
        Assert.StartsWith("https://image.tmdb.org/t/p/w342/", dune.PosterUrl);
        Assert.Equal(SeerrStatus.Unknown, dune.Status);
        Assert.True(dune.Status.CanRequest());
        Assert.Equal("Dune: Prophecy", page.Results[1].DisplayTitle);

        var owned = Read<SeerrTitle>("""{"id": 3034, "mediaType": "movie", "title": "Frankenstein", "mediaInfo": {"status": 5, "jellyfinMediaId": "9f36790b"}}""");
        Assert.Equal(SeerrStatus.Available, owned.Status);
        Assert.False(owned.Status.CanRequest());
    }

    [Fact]
    public void AShowsSeasons()
    {
        var fresh = Read<SeerrShow>("""{"seasons": [{"seasonNumber": 0, "episodeCount": 300, "name": "Specials"}, {"seasonNumber": 1, "episodeCount": 10}, {"seasonNumber": 2, "episodeCount": 10}], "mediaInfo": null}""").SeasonStatus();
        Assert.All(fresh, s => Assert.Equal(SeerrStatus.Unknown, s.Status));
        Assert.DoesNotContain(fresh, s => s.Number == 0);

        var partly = Read<SeerrShow>("""{"seasons": [{"seasonNumber": 0, "episodeCount": 3}, {"seasonNumber": 1, "episodeCount": 12}, {"seasonNumber": 2, "episodeCount": 12}, {"seasonNumber": 3, "episodeCount": 12}], "mediaInfo": {"status": 4, "seasons": [{"seasonNumber": 1, "status": 5}, {"seasonNumber": 2, "status": 2}]}}""").SeasonStatus();
        // The specials left out; season 1 there, 2 asked for, 3 open.
        Assert.Equal([1, 2, 3], partly.Select(s => s.Number));
        Assert.Equal([SeerrStatus.Available, SeerrStatus.Pending, SeerrStatus.Unknown], partly.Select(s => s.Status));
        Assert.Equal(12, partly[0].Episodes);
    }

    /// <summary>From the first of last month to six months on, whatever the day.</summary>
    [Fact]
    public void UpcomingAnimesWindow()
    {
        Assert.Equal(("2026-10-01", "2027-04-01"), SeerrClient.UpcomingWindow(new DateTime(2026, 11, 1, 6, 0, 0)));
        Assert.Equal(("2026-12-01", "2027-06-01"), SeerrClient.UpcomingWindow(new DateTime(2027, 1, 31)));
        Assert.Equal(("2026-09-20", "2026-11-01"), SeerrClient.TheatricalWindow(new DateTime(2026, 11, 1)));
    }

    /// <summary>Discover's rows may leave out what kind each title is; a row of one kind says.</summary>
    [Fact]
    public void DiscoverRows()
    {
        var title = Read<SeerrTitle>("""{"id":326247,"name":"Fool Night","firstAirDate":"2026-11-26","posterPath":"/x.jpg"}""");
        Assert.Equal("Fool Night", title.DisplayTitle);
        Assert.Null(title.MediaType);
        Assert.False(title.IsMovie);

        var sliders = Read<List<SeerrSlider>>("""[{"id":1,"type":4,"title":null,"enabled":true},{"id":2,"type":14,"title":"Action","data":"28,12","enabled":true},{"id":3,"type":8,"enabled":false}]""");
        Assert.Equal("Trending", SeerrClient.SliderTitle(sliders[0]));
        Assert.Equal("Action", SeerrClient.SliderTitle(sliders[1]));
        Assert.Equal(("/discover/movies?page=1&genre=28%2C12", SeerrKind.Movie), SeerrClient.TitlesPath(sliders[1]));
        // Studios are a row of companies, not titles.
        Assert.Null(SeerrClient.TitlesPath(sliders[2]));
        Assert.Equal(("/discover/tv?page=1&watchRegion=GB&watchProviders=8|337", SeerrKind.Tv),
            SeerrClient.TitlesPath(new SeerrSlider { Type = 21, Data = "GB,8|337" }));
    }

    [Fact]
    public void Requests()
    {
        var request = Read<SeerrRequest>("""{"id": 7, "status": 1, "type": "movie", "createdAt": "2026-11-01T12:31:29.000Z", "media": {"tmdbId": 438631, "mediaType": "movie", "status": 2}, "requestedBy": {"id": 3, "displayName": "Phillip"}}""");
        Assert.True(request.IsPending);
        Assert.Equal("Pending Approval", request.StateLabel);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 12, 31, 29, TimeSpan.Zero), request.Created);
        Assert.Equal("Available", (request with { Status = 2, Media = request.Media with { Status = SeerrStatus.Available } }).StateLabel);
        Assert.True(Read<SeerrUser>("""{"id": 1, "permissions": 2}""").ManagesRequests);
        Assert.False(Read<SeerrUser>("""{"id": 4, "permissions": 32}""").ManagesRequests);
    }

    [Fact]
    public void AddressesAsTyped()
    {
        Assert.Equal("http://192.168.1.240:5055/", SeerrClient.Address(" 192.168.1.240:5055/ ")?.AbsoluteUri);
        Assert.Equal("https://requests.example.com/", SeerrClient.Address("https://requests.example.com//")?.AbsoluteUri);
        Assert.Null(SeerrClient.Address("   "));
    }

    /// <summary>A copy for one server user opens only for that user, however the id's written.</summary>
    [Fact]
    public void ScrambledForOneUser()
    {
        var secrets = new JsonObject
        {
            ["aniList"] = new JsonObject { ["token"] = "abc.def.ghi", ["name"] = "Phillip" },
            ["seerr"] = new JsonObject { ["url"] = "http://192.168.1.240:5055", ["cookie"] = "connect.sid=s%3Aabc", ["name"] = "peppy", ["userId"] = 3 },
            ["updated"] = "2026-11-01T12:23:00Z",
        };
        var blob = TrackerVault.Seal(secrets, "5F2C-AB10");
        Assert.StartsWith("aster1:", blob);
        Assert.DoesNotContain("abc.def.ghi", blob);
        var opened = TrackerVault.Open(blob, "5f2cab10");
        Assert.NotNull(opened);
        Assert.Equal(secrets.ToJsonString(), opened.ToJsonString());
        Assert.Null(TrackerVault.Open(blob, "someone-else"));
        Assert.Null(TrackerVault.Open("not a blob", "5f2cab10"));
        Assert.Null(TrackerVault.Open("aster1:%%%", "5f2cab10"));
        // Sealed twice, two different blobs (a fresh nonce each time).
        Assert.NotEqual(blob, TrackerVault.Seal(secrets, "5f2cab10"));
    }
}

/// <summary>Discover's Movies and Series filters, as the Apple app's SeerrDiscoverTests check them.</summary>
public class SeerrFilterTests
{
    [Fact]
    public void AnUnfilteredGrid() =>
        Assert.Equal("/discover/movies?page=1&sortBy=popularity.desc", new SeerrFilter(SeerrKind.Movie).Path(1));

    [Fact]
    public void EveryFilterAsSeerrWritesIt()
    {
        var filter = new SeerrFilter(SeerrKind.Tv)
        {
            Sort = SeerrSort.Newest,
            Genres = new HashSet<int> { 16, 10759 },
            FromYear = 2020,
            ToYear = 2029,
            MinimumRating = 7,
            MinimumRuntime = 30,
            MaximumRuntime = 60,
            Language = "ja",
            Company = new SeerrCompany(213, "Netflix", null),
            Providers = new HashSet<int> { 8, 337 },
            Region = "US",
        };
        var path = filter.Path(2);
        Assert.StartsWith("/discover/tv?page=2&sortBy=first_air_date.desc", path);
        Assert.Contains("genre=16%2C10759", path);
        Assert.Contains("firstAirDateGte=2020-01-01&firstAirDateLte=2029-12-31", path);
        Assert.Contains("voteAverageGte=7&voteCountGte=50", path);
        Assert.Contains("withRuntimeGte=30&withRuntimeLte=60", path);
        Assert.Contains("language=ja", path);
        Assert.Contains("network=213", path);
        Assert.Contains("watchProviders=8|337&watchRegion=US", path);
        Assert.Equal(7, filter.ActiveCount);

        var film = new SeerrFilter(SeerrKind.Movie) { Company = new SeerrCompany(3, "Pixar", null), FromYear = 1990 };
        Assert.Contains("studio=3", film.Path(1));
        Assert.Contains("primaryReleaseDateGte=1990-01-01", film.Path(1));
    }

    [Fact]
    public void AnimeIsAnimationFromJapan()
    {
        var filter = new SeerrFilter(SeerrKind.Tv) { Genres = new HashSet<int> { 35 } }.WithAnime(true);
        Assert.True(filter.IsAnime);
        Assert.Contains("genre=16%2C35", filter.Path(1));
        Assert.Contains("language=ja", filter.Path(1));
        // Off takes both away, leaving the comedy chosen.
        var off = filter.WithAnime(false);
        Assert.False(off.IsAnime);
        Assert.Equal(new HashSet<int> { 35 }, off.Genres);
        Assert.Null(off.Language);
    }
}

/// <summary>When a requested film arrives at home (or in cinemas), from TMDB's release dates.</summary>
public class SeerrReleasesTests
{
    private static SeerrReleases Read(string json) => System.Text.Json.JsonSerializer.Deserialize<SeerrReleases>(json, SeerrClient.Json)!;
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstHomeReleaseAhead()
    {
        var releases = Read("""{"results":[{"iso_3166_1":"US","release_dates":[{"type":3,"release_date":"2026-10-17T00:00:00.000Z"},{"type":5,"release_date":"2027-01-20T00:00:00.000Z"},{"type":4,"release_date":"2026-12-01T00:00:00.000Z"}]}]}""");
        var arrival = releases.Arrival("US", Now);
        Assert.Equal((new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), "Digital Release"), arrival);
    }

    [Fact]
    public void OutAtHomeAlreadyIsLeftOut() =>
        Assert.Null(Read("""{"results":[{"iso_3166_1":"US","release_dates":[{"type":4,"release_date":"2026-10-01T00:00:00.000Z"}]}]}""").Arrival("US", Now));

    [Fact]
    public void OnlyACinemaDateAheadIsInTheaters()
    {
        var releases = Read("""{"results":[{"iso_3166_1":"GB","release_dates":[{"type":3,"release_date":"2026-12-19T00:00:00.000Z"}]}]}""");
        // No row for the region: the US's, and none there, so nothing; the GB one for GB.
        Assert.Null(releases.Arrival("US", Now));
        Assert.Equal("In Theaters", releases.Arrival("GB", Now)?.How);
    }
}
