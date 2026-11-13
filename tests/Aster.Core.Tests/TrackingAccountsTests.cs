using Aster.Core.Tracking;

namespace Aster.Core.Tests;

/// <summary>The tracking plugin's linked accounts, as /AsterTracking/Accounts writes them.</summary>
public class TrackingAccountsTests
{
    [Fact]
    public void ReadsTheAccountsAndLog()
    {
        var accounts = TrackingAccountsApi.ReadAccounts("""
            {"simkl":{"available":true,"linked":true,"name":"peppy","scrobble":true},
             "aniList":{"available":true,"linked":false},
             "lastFm":{"available":false},
             "waiting":2,
             "log":[{"at":"2026-11-10T21:04:05.123Z","service":"Simkl","title":"Frieren · S1, E3","result":"Sent"}]}
            """);
        Assert.True(accounts.Account(TrackedService.Simkl)!.IsLinked);
        Assert.Equal("peppy", accounts.Simkl!.Name);
        Assert.False(accounts.Account(TrackedService.AniList)!.IsLinked);
        Assert.False(accounts.LastFm!.Available);
        Assert.Null(accounts.Account(TrackedService.Hardcover));
        Assert.Equal(2, accounts.Waiting);
        Assert.Equal("Frieren · S1, E3", accounts.Log![0].Title);
        Assert.Equal(2026, TrackingAccountsApi.Date(accounts.Log[0].At)!.Value.Year);
    }

    [Fact]
    public void NamesAsThePluginDoes() =>
        Assert.Equal(["simkl", "anilist", "myanimelist", "lastfm", "listenbrainz", "hardcover"], Enum.GetValues<TrackedService>().Select(s => s.Key()));
}
