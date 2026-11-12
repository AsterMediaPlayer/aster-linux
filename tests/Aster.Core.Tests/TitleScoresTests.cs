using Aster.Core.Jellyfin;
using Aster.Core.Ratings;

namespace Aster.Core.Tests;

/// <summary>Titles' scores as each site writes them, and MDBList's over the server's. The Apple app's TitleScoresTests.</summary>
public class TitleScoresTests
{
    private const string Reply = """
        {"title":"Arrival","ratings":[
          {"source":"imdb","value":7.9,"score":79},
          {"source":"tomatoes","value":94,"score":94},
          {"source":"popcorn","value":82,"score":82},
          {"source":"metacritic","value":81,"score":81},
          {"source":"letterboxd","value":4.1,"score":82},
          {"source":"tmdb","value":76,"score":76},
          {"source":"trakt","value":80,"score":80},
          {"source":"rogerebert","value":null}
        ]}
        """;

    [Fact]
    public void ReadsMdbListsReplyAsTheSitesWriteIt()
    {
        var scores = MdbList.Parse(Reply);
        Assert.Equal(["94%", "82%", "7.9", "76%", "81", "4.1"], scores.Select(s => s.Text));
        Assert.True(scores[0].IsFresh);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(11.0)]
    public void NotAScore(double? value) => Assert.Null(TitleScore.Of(ScoreSource.Imdb, value, 10));

    [Fact]
    public void MdbListsOverTheServers()
    {
        var own = TitleScore.FromServer(new BaseItem { Id = "1", CriticRating = 90, CommunityRating = 7.2 });
        Assert.Equal(["90%", "7.2"], own.Select(s => s.Text));
        var merged = TitleScore.Merged(own, [TitleScore.Of(ScoreSource.Tomatometer, 94, 100)!, TitleScore.Of(ScoreSource.Imdb, 7.9, 10)!]);
        // The site's own Tomatometer replaces the server's copy; the unattributed one goes.
        Assert.Equal(["94%", "7.9"], merged.Select(s => s.Text));
    }

    [Fact]
    public void AskedByTmdbThenImdbThenTvdb()
    {
        Assert.Equal(("tmdb", "movie", "329865"), MdbList.Lookup(new BaseItem { Id = "1", Type = "Movie", ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt2543164", ["Tmdb"] = "329865" } }));
        Assert.Equal(("tvdb", "show", "81189"), MdbList.Lookup(new BaseItem { Id = "1", Type = "Series", ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" } }));
        Assert.Null(MdbList.Lookup(new BaseItem { Id = "1", Type = "Episode", ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" } }));
    }
}
