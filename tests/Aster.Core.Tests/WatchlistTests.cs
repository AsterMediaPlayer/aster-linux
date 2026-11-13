using System.Net;
using System.Text.Json;
using Aster.Core.Tracking;

namespace Aster.Core.Tests;

/// <summary>Add to Watchlist, through the Aster Discovery and Tracking plugin's /AsterTracking/Watchlist (0.9.8).</summary>
public sealed class WatchlistTests
{
    private static readonly TrackingFeatures WithWatchlist = new() { Watchlist = ["simkl"] };

    [Fact]
    public void ProviderIdsAreNamedAsThePluginNamesThem()
    {
        var ids = AsterTracking.TrackingIds(new Dictionary<string, string>
        {
            ["Tmdb"] = "1399", ["Imdb"] = "tt0944947", ["MyAnimeList"] = "5114", ["AniDB"] = " 69 ", ["Zap2It"] = "x", ["Tvdb"] = "",
        });
        Assert.Equal(new Dictionary<string, string> { ["tmdb"] = "1399", ["imdb"] = "tt0944947", ["mal"] = "5114", ["anidb"] = "69" }, ids);
    }

    [Fact]
    public async Task StateAsksWithTheKindAndIds()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, """{"Services":[{"Service":"simkl","Linked":true,"On":false},{"Service":"anilist","Linked":true,"On":true}]}"""));
        var state = await AsterTracking.WatchlistStateAsync(server.Client(), WithWatchlist, "movie", new Dictionary<string, string> { ["tmdb"] = "603", ["imdb"] = "tt0133093" });
        var only = Assert.Single(state);
        Assert.Equal("simkl", only.Service);
        Assert.Equal("Simkl", only.ServiceName);
        Assert.True(only.Linked);
        Assert.False(only.On);
        var query = server.Requests.Single().Url.Query;
        Assert.Contains("mediaType=movie", query);
        Assert.Contains("tmdb=603", query);
        Assert.Contains("imdb=tt0133093", query);
    }

    /// <summary>A plugin before 0.9.8 names no watchlists: the server isn't asked, and there's no button.</summary>
    [Fact]
    public async Task AnOlderPluginIsNotAsked()
    {
        var server = new StubServer(_ => (HttpStatusCode.OK, "{}"));
        Assert.Empty(await AsterTracking.WatchlistStateAsync(server.Client(), new TrackingFeatures(), "tv", new Dictionary<string, string> { ["tmdb"] = "1" }));
        Assert.Empty(await AsterTracking.WatchlistStateAsync(server.Client(), null, "tv", new Dictionary<string, string> { ["tmdb"] = "1" }));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AddingPostsTheTitleAndTakingOffDeletesIt()
    {
        var server = new StubServer(r => (HttpStatusCode.OK, r.Method == HttpMethod.Post ? """{"Service":"simkl","On":true}""" : """{"Service":"simkl","On":false}"""));
        var title = new WatchlistTitle { MediaType = "tv", Anime = true, Ids = new() { ["tmdb"] = "37854", ["mal"] = "21" }, Title = "One Piece", Year = 1999 };
        Assert.True(await AsterTracking.SetWatchlistAsync(server.Client(), "simkl", true, title));
        Assert.False(await AsterTracking.SetWatchlistAsync(server.Client(), "simkl", false, title));

        Assert.Equal(HttpMethod.Post, server.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, server.Requests[1].Method);
        Assert.EndsWith("/AsterTracking/Watchlist/simkl", server.Requests[0].Url.AbsolutePath);
        using var body = JsonDocument.Parse(server.Requests[1].Body!);
        Assert.Equal("tv", body.RootElement.GetProperty("MediaType").GetString());
        Assert.True(body.RootElement.GetProperty("Anime").GetBoolean());
        Assert.Equal("21", body.RootElement.GetProperty("Ids").GetProperty("mal").GetString());
        Assert.Equal(1999, body.RootElement.GetProperty("Year").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "Link Simkl first")]
    [InlineData(HttpStatusCode.Gone, "Simkl needs linking again.")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Simkl doesn't know this title.")]
    [InlineData(HttpStatusCode.BadGateway, "Simkl couldn't be reached. Try again.")]
    public async Task TheServersRefusalsAreSaidInWords(HttpStatusCode status, string words)
    {
        var server = new StubServer(_ => (status, """{"title":"x"}"""));
        var error = await Assert.ThrowsAsync<WatchlistException>(() =>
            AsterTracking.SetWatchlistAsync(server.Client(), "simkl", true, new WatchlistTitle { MediaType = "movie", Ids = new() { ["tmdb"] = "603" } }));
        Assert.StartsWith(words, error.Message);
    }
}
